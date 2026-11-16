using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Vigil.Core.Alerting;
using Vigil.Core.Notifications;
using Vigil.Core.Protocol;
using Xunit;

namespace Vigil.Core.Tests;

public sealed class NtfyClientTests
{
    private sealed class FakeNtfy(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Received { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Received.Add((request, await request.Content!.ReadAsStringAsync(cancellationToken)));
            return answer(request);
        }
    }

    private static NtfyClient Client(HttpMessageHandler handler) =>
        new(new HttpClient(handler), new Uri("https://ntfy.example/vigil-private-topic"), NullLogger<NtfyClient>.Instance);

    private static string Header(HttpRequestMessage request, string name) => request.Headers.GetValues(name).Single();

    [Fact]
    public async Task PostsTheMessageWithTitlePriorityAndTagsBySeverity()
    {
        using var ntfy = new FakeNtfy(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var client = Client(ntfy);

        await client.SendAsync(new Notification(new NewEvent(Severity.Critical, "fan-stalled:x", "Fan 'x' reads 0 RPM"), IsAllClear: false));
        await client.SendAsync(new Notification(new NewEvent(Severity.Warning, "ups-on-battery", "Mains power lost: ✓ ünïcode"), IsAllClear: false));

        var (critical, criticalBody) = ntfy.Received[0];
        Assert.Equal(HttpMethod.Post, critical.Method);
        Assert.Equal("https://ntfy.example/vigil-private-topic", critical.RequestUri?.ToString());
        Assert.Equal("Fan 'x' reads 0 RPM", criticalBody);
        Assert.Equal(("vigil: CRITICAL", "urgent", "rotating_light"), (Header(critical, "Title"), Header(critical, "Priority"), Header(critical, "Tags")));

        var (warning, warningBody) = ntfy.Received[1];
        Assert.Equal("Mains power lost: ✓ ünïcode", warningBody);
        Assert.Equal(("vigil: warning", "default", "warning"), (Header(warning, "Title"), Header(warning, "Priority"), Header(warning, "Tags")));
    }

    [Fact]
    public async Task AnAllClearReadsAsResolvedNotAsANewProblem()
    {
        using var ntfy = new FakeNtfy(_ => new HttpResponseMessage(HttpStatusCode.OK));

        await Client(ntfy).SendAsync(new Notification(new NewEvent(Severity.Info, "zfs-pool:tank", "Pool tank is ONLINE again."), IsAllClear: true));

        var (resolved, body) = Assert.Single(ntfy.Received);
        Assert.Equal("Pool tank is ONLINE again.", body);
        Assert.Equal(("vigil: resolved", "default", "white_check_mark"), (Header(resolved, "Title"), Header(resolved, "Priority"), Header(resolved, "Tags")));
    }

    /// <summary>A response body that fails the test if anything reads it.</summary>
    private sealed class UnreadableBody : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("the reply's body was read");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task TheRepliesBodyIsNeverRead()
    {
        // Only the status is used. Buffering the body, HttpClient's default, lets whatever
        // answers at NTFY_URL make vigil-core allocate as much as it cares to send.
        using var ntfy = new FakeNtfy(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new UnreadableBody()) });

        await Client(ntfy).SendAsync(new Notification(new NewEvent(Severity.Critical, "k", "m"), IsAllClear: false));

        Assert.Single(ntfy.Received);
    }

    [Fact]
    public async Task AFailureIsLoggedNeverThrown()
    {
        using var refusing = new FakeNtfy(_ => throw new HttpRequestException("connection refused"));
        using var rejecting = new FakeNtfy(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));

        await Client(refusing).SendAsync(new Notification(new NewEvent(Severity.Critical, "k", "m"), IsAllClear: false));
        await Client(rejecting).SendAsync(new Notification(new NewEvent(Severity.Critical, "k", "m"), IsAllClear: false));

        Assert.Single(rejecting.Received);
    }
}
