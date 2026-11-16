using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Vigil.Core.Api;
using Vigil.Core.Clients;
using Xunit;

namespace Vigil.Core.Tests;

public sealed class CloudflareRangesTests
{
    private const string Ipv4 = "173.245.48.0/20\n103.21.244.0/22\n104.16.0.0/13\n";
    private const string Ipv6 = "2400:cb00::/32\n2a06:98c0::/29";

    /// <summary>Cloudflare's two lists, answered from memory.</summary>
    private sealed class FakeCloudflare(Func<Uri, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(answer(request.RequestUri!));
        }
    }

    private static HttpResponseMessage Text(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static TrustedProxies Following()
    {
        Assert.True(TrustedProxies.TryParse("cloudflare", CloudflareRanges.BuiltIn, out var proxies, out _));
        return proxies;
    }

    private static CloudflareRangesRefresh Refresh(HttpMessageHandler cloudflare, TrustedProxies proxies) =>
        new(new CloudflareRanges(new HttpClient(cloudflare)), proxies, NullLogger<CloudflareRangesRefresh>.Instance, TimeProvider.System);

    [Fact]
    public void TheBuiltInRangesAreCloudflaresAndNothingWider()
    {
        // An edge address from each family, and one that is nobody's proxy.
        Assert.Contains(CloudflareRanges.BuiltIn, network => network.Contains(IPAddress.Parse("104.16.0.1")));
        Assert.Contains(CloudflareRanges.BuiltIn, network => network.Contains(IPAddress.Parse("2606:4700::6810:1")));
        Assert.DoesNotContain(CloudflareRanges.BuiltIn, network => network.Contains(IPAddress.Parse("127.0.0.1")));

        // The built-in list passes the checks a fetched one must.
        Assert.True(CloudflareRanges.TryParse(string.Join('\n', CloudflareRanges.BuiltIn), out var parsed, out var problem), problem);
        Assert.Equal(CloudflareRanges.BuiltIn, parsed);
    }

    [Theory]
    [InlineData("", "empty")]
    // An error page, a captive portal: anything that answers 200 and is not the list.
    [InlineData("<!doctype html><title>Just a moment...</title>", "not a network")]
    [InlineData("173.245.48.0/20\nnot-a-network\n", "not a network")]
    // A line that would have vigil-core believe a sixteenth of the internet, or all of it.
    [InlineData("0.0.0.0/0", "wider")]
    [InlineData("64.0.0.0/4", "wider")]
    [InlineData("2000::/3", "wider")]
    public void RefusesAListThatIsNotPlausiblyCloudflares(string body, string why)
    {
        Assert.False(CloudflareRanges.TryParse(body, out _, out var problem));
        Assert.Contains(why, problem, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesMoreNetworksThanCloudflareCouldPublish()
    {
        var body = string.Join('\n', Enumerable.Range(0, CloudflareRanges.MaximumNetworks + 1).Select(index => $"10.{index / 256}.{index % 256}.0/24"));

        Assert.False(CloudflareRanges.TryParse(body, out _, out var problem));
        Assert.Contains("more than", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFetchReplacesTheBuiltInRangesWithWhatIsPublished()
    {
        using var cloudflare = new FakeCloudflare(uri => Text(uri == CloudflareRanges.Ipv4List ? Ipv4 : Ipv6));
        var proxies = Following();

        Assert.True(await Refresh(cloudflare, proxies).RefreshOnceAsync(TestContext.Current.CancellationToken));

        Assert.Equal(
            ["173.245.48.0/20", "103.21.244.0/22", "104.16.0.0/13", "2400:cb00::/32", "2a06:98c0::/29"],
            proxies.Current.Select(network => network.ToString()));
        Assert.Equal(2, cloudflare.Requests);
    }

    [Fact]
    public async Task AFailureOfEitherListKeepsTheRangesHeld()
    {
        var proxies = Following();
        var held = proxies.Current;

        using var down = new FakeCloudflare(_ => throw new HttpRequestException("no route to host"));
        using var erroring = new FakeCloudflare(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var halfThere = new FakeCloudflare(uri => uri == CloudflareRanges.Ipv4List ? Text(Ipv4) : Text("<html>"));
        using var endless = new FakeCloudflare(_ => Text(new string('1', CloudflareRanges.MaximumListBytes + 1)));

        foreach (var cloudflare in new[] { down, erroring, halfThere, endless })
        {
            Assert.False(await Refresh(cloudflare, proxies).RefreshOnceAsync(TestContext.Current.CancellationToken));
            Assert.Same(held, proxies.Current);
        }
    }
}
