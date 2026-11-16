using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Hosting;
using Vigil.Core.Api;
using Vigil.Core.Configuration;

namespace Vigil.Core.Clients;

/// <summary>
/// The networks Cloudflare's edge connects from, as Cloudflare publishes them, for
/// <c>TRUSTED_PROXIES=cloudflare</c> (ADR-024).
/// </summary>
/// <remarks>
/// <para>
/// Two plain-text lists, one network a line. Cloudflare's own advice is to fetch them rather
/// than copy them. They decide whose <c>X-Forwarded-For</c> is believed, so they are read as
/// untrusted input like anything else (docs/SECURITY.md §4): over HTTPS, bounded in time and
/// size, and refused whole if a line is not a network, if there are implausibly many, or if
/// one is implausibly wide. A list that is refused, or cannot be fetched, changes nothing.
/// </para>
/// <para>
/// <see cref="BuiltIn"/> is what is trusted until the first fetch succeeds, so starting does
/// not wait on a third party and a host that cannot reach it still works.
/// </para>
/// </remarks>
public sealed class CloudflareRanges(HttpClient http)
{
    public static readonly Uri Ipv4List = new("https://www.cloudflare.com/ips-v4");
    public static readonly Uri Ipv6List = new("https://www.cloudflare.com/ips-v6");

    /// <summary>Each list is a few hundred bytes; past this it is not the list.</summary>
    public const int MaximumListBytes = 16 * 1024;

    /// <summary>Cloudflare publishes fifteen IPv4 networks and seven IPv6; past this it is not the list.</summary>
    public const int MaximumNetworks = 256;

    /// <summary>
    /// The widest network believed, by family. Cloudflare's widest are a /13 and a /29; a /8 or
    /// a /16 leaves room for it to grow, and still refuses a line that would trust a large
    /// part of the internet.
    /// </summary>
    public const int MinimumIpv4Prefix = 8;

    /// <inheritdoc cref="MinimumIpv4Prefix"/>
    public const int MinimumIpv6Prefix = 16;

    public static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(10);

    /// <summary>As published on 2026-11-16. Only what holds until the first fetch.</summary>
    public static readonly IReadOnlyList<IPNetwork> BuiltIn =
    [
        .. new[]
        {
            "173.245.48.0/20", "103.21.244.0/22", "103.22.200.0/22", "103.31.4.0/22", "141.101.64.0/18",
            "108.162.192.0/18", "190.93.240.0/20", "188.114.96.0/20", "197.234.240.0/22", "198.41.128.0/17",
            "162.158.0.0/15", "104.16.0.0/13", "104.24.0.0/14", "172.64.0.0/13", "131.0.72.0/22",
            "2400:cb00::/32", "2606:4700::/32", "2803:f800::/32", "2405:b500::/32", "2405:8100::/32",
            "2a06:98c0::/29", "2c0f:f248::/32",
        }.Select(IPNetwork.Parse),
    ];

    /// <summary>
    /// The networks in one published list. False, with why, unless every line is a plausible
    /// network: a list is taken whole or not at all, since half of one would stop believing
    /// part of Cloudflare.
    /// </summary>
    public static bool TryParse(string body, out IReadOnlyList<IPNetwork> networks, out string problem)
    {
        ArgumentNullException.ThrowIfNull(body);

        var parsed = new List<IPNetwork>();
        networks = parsed;

        foreach (var line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!IPNetwork.TryParse(line, out var network))
            {
                problem = "a line is not a network";
                return false;
            }

            var narrowest = network.BaseAddress.AddressFamily == AddressFamily.InterNetworkV6 ? MinimumIpv6Prefix : MinimumIpv4Prefix;
            if (network.PrefixLength < narrowest)
            {
                problem = $"{network} is wider than any network Cloudflare would publish";
                return false;
            }

            if (parsed.Count == MaximumNetworks)
            {
                problem = $"more than {MaximumNetworks} networks";
                return false;
            }

            parsed.Add(network);
        }

        problem = parsed.Count == 0 ? "the list is empty" : string.Empty;
        return parsed.Count > 0;
    }

    /// <summary>
    /// Both lists, fetched now. Throws <see cref="HttpRequestException"/>, <see cref="IOException"/>
    /// or <see cref="TimeoutException"/> when either cannot be had or is not a list: one family
    /// without the other is not Cloudflare's ranges.
    /// </summary>
    public async Task<IReadOnlyList<IPNetwork>> FetchAsync(CancellationToken cancellationToken)
    {
        return [.. await FetchListAsync(Ipv4List, cancellationToken).ConfigureAwait(false), .. await FetchListAsync(Ipv6List, cancellationToken).ConfigureAwait(false)];
    }

    private async Task<IReadOnlyList<IPNetwork>> FetchListAsync(Uri list, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(FetchTimeout);

        try
        {
            // ResponseHeadersRead, so the size is bounded here rather than buffered first.
            using var response = await http.GetAsync(list, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStreamAsync(limit.Token).ConfigureAwait(false);
            await using var bounded = new ReadBudgetStream(body);
            bounded.Allow(MaximumListBytes);
            using var reader = new StreamReader(bounded, Encoding.UTF8);

            return TryParse(await reader.ReadToEndAsync(limit.Token).ConfigureAwait(false), out var networks, out var problem)
                ? networks
                : throw new IOException($"{list.AbsolutePath} is not a list of networks: {problem}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"{list.AbsolutePath} did not arrive within {FetchTimeout.TotalSeconds:0}s");
        }
    }
}

/// <summary>
/// Keeps <see cref="TrustedProxies"/> in step with Cloudflare's published ranges. Registered
/// only when <c>TRUSTED_PROXIES</c> names <c>cloudflare</c>.
/// </summary>
/// <remarks>
/// A failure is logged and retried, never fatal, and leaves the ranges as they were: being a
/// day behind Cloudflare costs nothing, and trusting nobody would count every visitor as one
/// client.
/// </remarks>
public sealed class CloudflareRangesRefresh(CloudflareRanges ranges, TrustedProxies proxies, ILogger<CloudflareRangesRefresh> logger, TimeProvider time)
    : BackgroundService
{
    /// <summary>Cloudflare changes these a few times a year and announces it weeks ahead.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromHours(24);

    /// <summary>Sooner after a failure, which is usually the network not being up yet at boot.</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(15);

    private string _lastError = string.Empty;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var fetched = await RefreshOnceAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(fetched ? Every : RetryAfter, time, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The host is stopping.
        }
    }

    /// <summary>One fetch. False when it failed, which changes nothing.</summary>
    internal async Task<bool> RefreshOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var current = await ranges.FetchAsync(cancellationToken).ConfigureAwait(false);
            if (proxies.ReplaceCloudflare(current) && logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("trusting Cloudflare's published ranges: {Count} networks", current.Count);
            }

            _lastError = string.Empty;
            return true;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or TimeoutException)
        {
            // Once per distinct error, not once per attempt.
            if (error.Message != _lastError)
            {
                logger.LogWarning("could not refresh Cloudflare's ranges, keeping the ones held: {Error}", error.Message);
                _lastError = error.Message;
            }

            return false;
        }
    }
}
