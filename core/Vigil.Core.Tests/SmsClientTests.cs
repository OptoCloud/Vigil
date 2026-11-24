using System.Net;
using System.Text.Json;
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

    private static SmsClient Client(HttpMessageHandler handler) => new(new HttpClient(handler), Gateway, Token);

    private static Notification Raised(Severity severity, string message) => new(new NewEvent(severity, "k", message), IsAllClear: false);

    [Fact]
    public async Task PostsTheMessageAsJsonWithTheBearerToken()
    {
        using var gateway = new FakeGateway(_ => new HttpResponseMessage(HttpStatusCode.Accepted));

        Assert.Null(await Client(gateway).TryDeliverAsync(Raised(Severity.Critical, "Pool tank is DEGRADED. ✓ ünïcode"), TestContext.Current.CancellationToken));

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
        Assert.Equal("vigil CRITICAL: m", Raised(Severity.Critical, "m").Headline);
        Assert.Equal("vigil warning: m", Raised(Severity.Warning, "m").Headline);
        Assert.Equal("vigil resolved: m", new Notification(new NewEvent(Severity.Info, "k", "m"), IsAllClear: true).Headline);
    }

    [Fact]
    public async Task AFailureIsReportedWithoutTheUrlOrTheTokenAndNeverThrown()
    {
        using var refusing = new FakeGateway(_ => throw new HttpRequestException("connection refused"));
        using var rejecting = new FakeGateway(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var silent = new FakeGateway(_ => throw new TaskCanceledException());

        string?[] failures =
        [
            await Client(refusing).TryDeliverAsync(Raised(Severity.Critical, "m"), TestContext.Current.CancellationToken),
            await Client(rejecting).TryDeliverAsync(Raised(Severity.Critical, "m"), TestContext.Current.CancellationToken),
            await Client(silent).TryDeliverAsync(Raised(Severity.Critical, "m"), TestContext.Current.CancellationToken),
        ];

        Assert.Contains("401", failures[1], StringComparison.Ordinal);
        Assert.All(failures, failure =>
        {
            Assert.NotNull(failure);
            Assert.DoesNotContain(Token, failure, StringComparison.Ordinal);
            Assert.DoesNotContain("sms-gateway", failure, StringComparison.Ordinal);
            Assert.DoesNotContain("private-path", failure, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task StoppingIsNotReportedAsAFailure()
    {
        using var gateway = new FakeGateway(_ => new HttpResponseMessage(HttpStatusCode.Accepted));
        using var stopped = new CancellationTokenSource();
        await stopped.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(gateway).TryDeliverAsync(Raised(Severity.Critical, "m"), stopped.Token));
    }

    [Fact]
    public void ItCarriesWarningsAndWorse()
    {
        using var gateway = new FakeGateway(_ => new HttpResponseMessage(HttpStatusCode.Accepted));
        Assert.Equal((Severity.Warning, "sms"), (Client(gateway).MinimumSeverity, Client(gateway).Name));
    }
}
