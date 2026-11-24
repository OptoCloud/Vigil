using System.Net;
using Microsoft.Extensions.Logging;
using Vigil.Core.Clients;
using Vigil.Core.Configuration;
using Vigil.Core.Runtime;
using Xunit;

namespace Vigil.Core.Tests;

public sealed class HeartbeatTests
{
    private static readonly Uri Check = new("https://heartbeat.example/private-check-id");

    private sealed class FakeService(Func<HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Received { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Received.Add(request);
            return Task.FromResult(answer());
        }
    }

    private sealed class RecordingLogger : ILogger<Heartbeat>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
    }

    private readonly ManualTime _time = new();
    private readonly RecordingLogger _logger = new();
    private readonly RuntimePulse _pulse;

    public HeartbeatTests() => _pulse = new RuntimePulse(_time);

    private Heartbeat Beating(HttpMessageHandler handler) =>
        new(new HttpClient(handler), Check, TimeSpan.FromSeconds(60), _pulse, _logger, _time);

    [Fact]
    public async Task ItIsSentWhileTheLoopTurnsAndNotOnceItHasStopped()
    {
        using var service = new FakeService(() => new HttpResponseMessage(HttpStatusCode.OK));
        using var heartbeat = Beating(service);

        // Before the loop's first turn there is nothing to vouch for.
        Assert.False(await heartbeat.BeatOnceAsync(TestContext.Current.CancellationToken));
        Assert.Empty(service.Received);

        _pulse.Record(daemonConnected: false, databaseReady: false, RuntimeView.Empty);
        Assert.True(await heartbeat.BeatOnceAsync(TestContext.Current.CancellationToken));
        var request = Assert.Single(service.Received);
        Assert.Equal((HttpMethod.Get, Check), (request.Method, request.RequestUri));

        _time.Advance(Limits.LoopStalledAfter + TimeSpan.FromSeconds(1));
        Assert.False(await heartbeat.BeatOnceAsync(TestContext.Current.CancellationToken));
        Assert.Single(service.Received);
    }

    [Fact]
    public async Task AFailureIsLoggedOnceWithoutTheUrlAndSoIsItsEnd()
    {
        var answers = new Queue<Func<HttpResponseMessage>>(
        [
            () => new HttpResponseMessage(HttpStatusCode.NotFound),
            () => new HttpResponseMessage(HttpStatusCode.NotFound),
            () => throw new HttpRequestException("connection refused"),
            () => new HttpResponseMessage(HttpStatusCode.OK),
            () => new HttpResponseMessage(HttpStatusCode.OK),
        ]);
        using var service = new FakeService(() => answers.Dequeue()());
        using var heartbeat = Beating(service);
        _pulse.Record(daemonConnected: true, databaseReady: true, RuntimeView.Empty);

        while (answers.Count > 0)
        {
            await heartbeat.BeatOnceAsync(TestContext.Current.CancellationToken);
        }

        // 404 once though it happened twice, then the different failure, then the recovery, once.
        Assert.Equal(3, _logger.Lines.Count);
        Assert.Contains("404", _logger.Lines[0], StringComparison.Ordinal);
        Assert.Contains("connection refused", _logger.Lines[1], StringComparison.Ordinal);
        Assert.Contains("accepted again", _logger.Lines[2], StringComparison.Ordinal);
        Assert.All(_logger.Lines, line =>
        {
            Assert.DoesNotContain("heartbeat.example", line, StringComparison.Ordinal);
            Assert.DoesNotContain("private-check-id", line, StringComparison.Ordinal);
        });
    }
}
