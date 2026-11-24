using Microsoft.Extensions.Logging;
using Vigil.Core.Alerting;
using Vigil.Core.Notifications;
using Vigil.Core.Protocol;
using Xunit;

namespace Vigil.Core.Tests;

public sealed class RetryingNotifierTests
{
    /// <summary>A channel that fails as many times as it is told to, and records every attempt.</summary>
    private sealed class FlakyDelivery(int failures) : IDelivery
    {
        private int _remaining = failures;

        public List<string> Attempts { get; } = [];

        /// <summary>Completes when the message with this text has been delivered or given up on.</summary>
        public TaskCompletionSource Settled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string SettleOn { get; init; } = "last";

        public string Name => "flaky";

        public Severity MinimumSeverity => Severity.Warning;

        public Task<string?> TryDeliverAsync(Notification notification, CancellationToken cancellationToken)
        {
            Attempts.Add(notification.Event.Message);
            var failed = _remaining-- > 0;
            if (!failed && notification.Event.Message == SettleOn)
            {
                Settled.TrySetResult();
            }

            return Task.FromResult(failed ? "the other side said no" : null);
        }
    }

    /// <summary>A clock whose timers fire at once, and which remembers how long each was meant to wait.</summary>
    private sealed class ImpatientTime : TimeProvider
    {
        public List<TimeSpan> Waits { get; } = [];

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (Waits)
            {
                Waits.Add(dueTime);
            }

            return base.CreateTimer(callback, state, TimeSpan.Zero, period);
        }
    }

    private sealed class RecordingLogger : ILogger<RetryingNotifier>
    {
        public List<(LogLevel Level, string Line)> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Lines)
            {
                Lines.Add((logLevel, formatter(state, exception)));
            }
        }
    }

    private static Notification About(string message) => new(new NewEvent(Severity.Critical, "k", message), IsAllClear: false);

    private static Task Within(Task task) => task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

    [Fact]
    public async Task AFailedAttemptIsRetriedAfterEachWaitInTurn()
    {
        var delivery = new FlakyDelivery(failures: 2);
        var time = new ImpatientTime();
        var logger = new RecordingLogger();
        await using var notifier = new RetryingNotifier(delivery, logger, time);

        notifier.Send(About("last"));
        await Within(delivery.Settled.Task);

        Assert.Equal(["last", "last", "last"], delivery.Attempts);
        Assert.Equal(RetryingNotifier.RetryDelays.Take(2), time.Waits);
        Assert.Equal([LogLevel.Warning, LogLevel.Warning], logger.Lines.Select(line => line.Level));
        Assert.All(logger.Lines, line => Assert.Contains("the other side said no", line.Line, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ItGivesUpAfterTheLastWaitAndCarriesOnWithTheNext()
    {
        // One more failure than there are attempts for a single notification.
        var attempts = RetryingNotifier.RetryDelays.Count + 1;
        var delivery = new FlakyDelivery(failures: attempts);
        var logger = new RecordingLogger();
        await using var notifier = new RetryingNotifier(delivery, logger, new ImpatientTime());

        notifier.Send(About("lost"));
        notifier.Send(About("last"));
        await Within(delivery.Settled.Task);

        Assert.Equal([.. Enumerable.Repeat("lost", attempts), "last"], delivery.Attempts);
        Assert.Equal(LogLevel.Error, logger.Lines[^1].Level);
        Assert.Contains("gave up", logger.Lines[^1].Line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhatIsBehindARetryWaitsItsTurn()
    {
        // "resolved" must not overtake the problem it resolves.
        var delivery = new FlakyDelivery(failures: 1);
        await using var notifier = new RetryingNotifier(delivery, new RecordingLogger(), new ImpatientTime());

        notifier.Send(About("problem"));
        notifier.Send(About("last"));
        await Within(delivery.Settled.Task);

        Assert.Equal(["problem", "problem", "last"], delivery.Attempts);
    }

    [Fact]
    public async Task ItIsNamedAndRatedAsItsChannelAndStopsWhileWaiting()
    {
        var delivery = new FlakyDelivery(failures: int.MaxValue);
        // The real clock: the first wait is thirty seconds, which disposal must not sit out.
        var notifier = new RetryingNotifier(delivery, new RecordingLogger(), TimeProvider.System);
        Assert.Equal(("flaky", Severity.Warning), (notifier.Name, notifier.MinimumSeverity));

        notifier.Send(About("never"));
        await Within(notifier.DisposeAsync().AsTask());
    }
}
