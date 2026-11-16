using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Vigil.Core.Api;
using Vigil.Core.Clients;
using Vigil.Core.Configuration;
using Xunit;

namespace Vigil.Core.Tests;

/// <summary>Whose request it is: the only identity a public, accountless dashboard has (ADR-023).</summary>
public sealed class AdmissionTests
{
    [Theory]
    [InlineData("203.0.113.7", "203.0.113.7")]
    [InlineData("::ffff:203.0.113.7", "203.0.113.7")]
    // One IPv6 customer holds a whole /64, so every address in it is the same client.
    [InlineData("2001:db8:1:2:aaaa:bbbb:cccc:dddd", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:2::1", "2001:db8:1:2::/64")]
    public void AClientIsItsAddressOrItsIpv6Network(string address, string expected)
    {
        Assert.Equal(expected, Admission.ClientKey(IPAddress.Parse(address)));
    }

    private static TrustedProxies Proxies(string? value)
    {
        Assert.True(TrustedProxies.TryParse(value, CloudflareRanges.BuiltIn, out var proxies, out var problem), problem);
        return proxies;
    }

    [Fact]
    public void ParsesAddressesNetworksAndTheCloudflareKeyword()
    {
        Assert.Equal(["127.0.0.1/32", "10.0.0.0/24", "::1/128"], Proxies(" 127.0.0.1, 10.0.0.0/24 ,::1").Current.Select(network => network.ToString()));
        Assert.Empty(Proxies(null).Current);
        Assert.False(Proxies("127.0.0.1").FollowsCloudflare);

        // The keyword stands for the built-in ranges until a refresh replaces them, next to
        // whatever else is named: a tunnel's daemon on this host, say.
        var both = Proxies("127.0.0.1,Cloudflare");
        Assert.True(both.FollowsCloudflare);
        Assert.Equal(1 + CloudflareRanges.BuiltIn.Count, both.Current.Count);
    }

    [Theory]
    [InlineData("127.0.0.1,proxy.lan", "'proxy.lan'")]
    // Believing anyone is the same as not checking.
    [InlineData("0.0.0.0/0", "'0.0.0.0/0'")]
    [InlineData("::/0", "'::/0'")]
    public void RefusesANameOrANetworkThatIsEveryone(string value, string named)
    {
        Assert.False(TrustedProxies.TryParse(value, CloudflareRanges.BuiltIn, out _, out var problem));
        Assert.Contains(named, problem, StringComparison.Ordinal);
    }

    [Fact]
    public void ARefreshReplacesCloudflaresPartAndKeepsWhatWasNamed()
    {
        var proxies = Proxies("127.0.0.1,cloudflare");
        var before = proxies.Current;

        Assert.True(proxies.ReplaceCloudflare([IPNetwork.Parse("198.51.100.0/24")]));
        Assert.Equal(["127.0.0.1/32", "198.51.100.0/24"], proxies.Current.Select(network => network.ToString()));

        // A new list, not an edit of the old one: a request reading the old one is unaffected.
        Assert.Equal(1 + CloudflareRanges.BuiltIn.Count, before.Count);

        // The same ranges again is no change, and without the keyword nothing is ever added.
        Assert.False(proxies.ReplaceCloudflare([IPNetwork.Parse("198.51.100.0/24")]));
        Assert.False(Proxies("127.0.0.1").ReplaceCloudflare([IPNetwork.Parse("198.51.100.0/24")]));
    }

    /// <summary>What the app saw of one request sent with the given forwarding headers.</summary>
    private static async Task<string> SeenAsync(string? trustedProxies, string forwardedFor)
    {
        var proxies = Proxies(trustedProxies);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        await using var app = builder.Build();
        app.UseForwardedClient(proxies);
        app.MapGet("/", (HttpContext context) => $"{Admission.ClientKey(context)} {context.Request.Scheme}");
        await app.StartAsync(TestContext.Current.CancellationToken);

        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add("X-Forwarded-For", forwardedFor);
        request.Headers.Add("X-Forwarded-Proto", "https");
        using var response = await http.SendAsync(request, TestContext.Current.CancellationToken);
        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ForwardedHeadersAreAClaimUnlessTheConnectionIsFromANamedProxy()
    {
        // Nothing named: a client cannot give itself a new address, or HTTPS, with a header.
        Assert.Equal("127.0.0.1 http", await SeenAsync(null, "198.51.100.4"));
        Assert.Equal("127.0.0.1 http", await SeenAsync("10.9.9.9", "198.51.100.4"));

        // The test connects from loopback, so naming loopback makes it the proxy.
        Assert.Equal("198.51.100.4 https", await SeenAsync("127.0.0.1", "198.51.100.4"));
    }

    [Fact]
    public async Task OnlyTheHopTheProxyAddedIsBelieved()
    {
        // A proxy appends the address it saw; whatever the client wrote before it stays a claim.
        Assert.Equal("198.51.100.4 https", await SeenAsync("127.0.0.1", "1.2.3.4, 198.51.100.4"));
    }

    [Fact]
    public async Task ARefreshedRangeIsBelievedWithoutARestart()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        // Cloudflare's ranges do not include loopback, where this test connects from.
        var proxies = Proxies("cloudflare");
        await using var app = builder.Build();
        app.UseForwardedClient(proxies);
        app.MapGet("/", (HttpContext context) => Admission.ClientKey(context));
        await app.StartAsync(TestContext.Current.CancellationToken);

        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        http.DefaultRequestHeaders.Add("X-Forwarded-For", "198.51.100.4");

        Assert.Equal("127.0.0.1", await http.GetStringAsync(new Uri("/", UriKind.Relative), TestContext.Current.CancellationToken));

        // As if Cloudflare had published a new range, and this connection came from it.
        proxies.ReplaceCloudflare([IPNetwork.Parse("127.0.0.0/8")]);
        Assert.Equal("198.51.100.4", await http.GetStringAsync(new Uri("/", UriKind.Relative), TestContext.Current.CancellationToken));
    }
}
