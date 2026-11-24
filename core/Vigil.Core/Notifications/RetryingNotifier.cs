using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Vigil.Core.Protocol;

namespace Vigil.Core.Notifications;

/// <summary>
/// A channel as the runtime sees it: <see cref="Send"/> queues and returns, and one loop
/// delivers in order, trying again when an attempt fails. A text exists for the moment the
/// network is unwell, which is exactly when a single attempt is most likely to be lost.
/// </summary>
/// <remarks>
/// In order, because "resolved" must not arrive before the problem it resolves: a notification
/// that is being retried holds back the ones behind it. Both the retries and the queue are
/// bounded (docs/SECURITY.md §4.2), so a channel that is down for a day costs a fixed amount
/// of memory and then loses the oldest, with a line in the log for each. What is queued is in
/// memory, as what <see cref="AlertRouter"/> remembers is: a restart forgets it, and re-raises
/// whatever still holds.
/// </remarks>
public sealed class RetryingNotifier : INotifier, IAsyncDisposable
{
    /// <summary>
    /// The waits between attempts, after which the notification is dropped. About a quarter of
    /// an hour in all: long enough for a modem to re-register or a mail server to restart, and
    /// short enough that what finally arrives still describes the present.
    /// </summary>
    public static readonly IReadOnlyList<TimeSpan> RetryDelays = [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10)];

    /// <summary>
    /// Notifications waiting for one channel. Far more than a bad night raises; past it the
    /// oldest is dropped, since the newest says more about the host as it is now.
    /// </summary>
    public const int MaximumQueued = 64;

    private readonly IDelivery _delivery;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly Channel<Notification> _queue;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _loop;

    public RetryingNotifier(IDelivery delivery, ILogger<RetryingNotifier> logger, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        _delivery = delivery;
        _logger = logger;
        _time = time;
        _queue = Channel.CreateBounded<Notification>(
            new BoundedChannelOptions(MaximumQueued) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest },
            _ => _logger.LogError("dropped a notification: more than {Limit} are waiting for {Channel}", MaximumQueued, _delivery.Name));
        _loop = Task.Run(RunAsync);
    }

    public string Name => _delivery.Name;

    public Severity MinimumSeverity => _delivery.MinimumSeverity;

    /// <summary>Queues and returns at once. Nothing is thrown: a failure is the loop's to log.</summary>
    public void Send(Notification notification) => _queue.Writer.TryWrite(notification);

    private async Task RunAsync()
    {
        try
        {
            await foreach (var notification in _queue.Reader.ReadAllAsync(_stopping.Token).ConfigureAwait(false))
            {
                await DeliverAsync(notification, _stopping.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping: what is still queued is lost with the process, as documented above.
        }
    }

    private async Task DeliverAsync(Notification notification, CancellationToken stopping)
    {
        for (var attempt = 0; ; attempt++)
        {
            if (await _delivery.TryDeliverAsync(notification, stopping).ConfigureAwait(false) is not { } failure)
            {
                return;
            }

            if (attempt == RetryDelays.Count)
            {
                _logger.LogError("gave up on a notification over {Channel} after {Attempts} attempts: {Failure}", _delivery.Name, attempt + 1, failure);
                return;
            }

            var delay = RetryDelays[attempt];
            _logger.LogWarning("could not notify over {Channel}: {Failure}. Trying again in {Seconds} s", _delivery.Name, failure, (int)delay.TotalSeconds);
            await Task.Delay(delay, _time, stopping).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _stopping.CancelAsync().ConfigureAwait(false);
        await _loop.ConfigureAwait(false);
        _stopping.Dispose();
    }
}
