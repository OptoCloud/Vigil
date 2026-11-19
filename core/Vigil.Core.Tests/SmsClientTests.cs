using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Vigil.Core.Alerting;
using Vigil.Core.Notifications;
using Vigil.Core.Protocol;
using Xunit;

namespace Vigil.Core.Tests;

public sealed class SmsClientTests
{
    private const string Token = "s3cret-gateway-token";
    private static readonly Uri Gateway = new("http://sms-gateway.lan:8080/private-path/send");

    private sealed class FakeGateway(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Received { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Received.Add((request, await request.Content!.ReadAsStringAsync(cancellationToken)));
            return answer(request);
        }
    }

    /// <summary>Every line logged, formatted, so a test can say what must never be in one.</summary>
    private sealed class RecordingLogger : ILogger<SmsClient>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
    }

    private static SmsClient Client(HttpMessageHandler handler, ILogger<SmsClient>? logger = null) =>
        new(new HttpClient(handler), Gateway, Token, logger ?? new RecordingLogger());

    private static Notification Raised(Severity severity, string message) => new(new NewEvent(severity, "k", message), IsAllClear: false);

    [Fact]
    public async Task PostsTheMessageAsJsonWithTheBearerToken()
    {
        using var gateway = new FakeGateway(_ => new HttpResponseMessage(HttpStatusCode.Accepted));

        await Client(gateway).SendAsync(Raised(Severity.Critical, "Pool tank is DEGRADED. ✓ ünïcode"));

        var (request, body) = Assert.Single(gateway.Received);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(Gateway, request.RequestUri);
        Assert.Equal(("Bearer", Token), (request.Headers.Authorization?.Scheme, request.Headers.Authorization?.Parameter));
        Assert.Equal("application/json", request.Content!.Headers.ContentType?.MediaType);
        Assert.Equal("vigil CRITICAL: Pool tank is DEGRADED. ✓ ünïcode", JsonElement.Parse(body).GetProperty("message").GetString());
    }

    [Fact]
    public void EachTextSaysHowBadItIsOrThatItIsOver()
    {
        Assert.Equal("vigil CRITICAL: m", SmsClient.TextOf(Raised(Severity.Critical, "m")));
        Assert.Equal("vigil warning: m", SmsClient.TextOf(Raised(Severity.Warning, "m")));
        Assert.Equal("vigil resolved: m", SmsClient.TextOf(new Notification(new NewEvent(Severity.Info, "k", "m"), IsAllClear: true)));
    }

    [Fact]
    public async Task AFailureIsLoggedWithoutTheUrlOrTheTokenAndNeverThrown()
    {
        var logger = new RecordingLogger();
        using var refusing = new FakeGateway(_ => throw new HttpRequestException("connection refused"));
        using var rejecting = new FakeGateway(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        await Client(refusing, logger).SendAsync(Raised(Severity.Critical, "m"));
        await Client(rejecting, logger).SendAsync(Raised(Severity.Critical, "m"));

        Assert.Equal(2, logger.Lines.Count);
        Assert.Contains("401", logger.Lines[1], StringComparison.Ordinal);
        Assert.All(logger.Lines, line =>
        {
            Assert.DoesNotContain(Token, line, StringComparison.Ordinal);
            Assert.DoesNotContain("sms-gateway", line, StringComparison.Ordinal);
            Assert.DoesNotContain("private-path", line, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void ItCarriesWarningsAndWorse()
    {
        using var gateway = new FakeGateway(_ => new HttpResponseMessage(HttpStatusCode.Accepted));
        Assert.Equal((Severity.Warning, "sms"), (Client(gateway).MinimumSeverity, Client(gateway).Name));
    }
}
