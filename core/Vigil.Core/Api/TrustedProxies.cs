using System.Net;
using System.Net.Sockets;

namespace Vigil.Core.Api;

/// <summary>
/// Whose word vigil-core takes for a visitor's address: the proxies named in
/// <c>TRUSTED_PROXIES</c>, as addresses, networks, or the word <c>cloudflare</c>.
/// </summary>
/// <remarks>
/// <para>
/// An address or a network is whatever the operator runs in front: nginx on this host, a
/// tunnel's daemon, a load balancer's subnet. <c>cloudflare</c> stands for the ranges Cloudflare
/// publishes, which is the only honest way to name it: its edge connects from some fifteen
/// networks that it changes without notice, so a list typed into an env file is wrong one day
/// and nobody is told. <see cref="Clients.CloudflareRanges"/> keeps that part current (ADR-024).
/// </para>
/// <para>
/// The set is replaced whole, never edited, because requests read it while the refresh
/// writes it: a reader holds one snapshot or the next.
/// </para>
/// </remarks>
public sealed class TrustedProxies
{
    /// <summary>The entry that stands for Cloudflare's published ranges.</summary>
    public const string CloudflareKeyword = "cloudflare";

    private readonly IReadOnlyList<IPNetwork> _named;
    private IReadOnlyList<IPNetwork> _current;

    private TrustedProxies(IReadOnlyList<IPNetwork> named, bool followsCloudflare)
    {
        _named = named;
        _current = named;
        FollowsCloudflare = followsCloudflare;
    }

    /// <summary>Whether <c>cloudflare</c> was named, and so whether anything refreshes this.</summary>
    public bool FollowsCloudflare { get; }

    /// <summary>Every network trusted right now. A new list each time it changes.</summary>
    public IReadOnlyList<IPNetwork> Current => Volatile.Read(ref _current);

    /// <summary>
    /// Replaces Cloudflare's part of the set. False, and nothing changes, when it is what is
    /// already held or <c>cloudflare</c> was never named.
    /// </summary>
    public bool ReplaceCloudflare(IReadOnlyList<IPNetwork> ranges)
    {
        ArgumentNullException.ThrowIfNull(ranges);

        IReadOnlyList<IPNetwork> next = [.. _named, .. ranges];
        if (!FollowsCloudflare || next.SequenceEqual(Current))
        {
            return false;
        }

        Volatile.Write(ref _current, next);
        return true;
    }

    /// <summary>
    /// <c>TRUSTED_PROXIES</c> as a set, holding <paramref name="cloudflare"/> for the keyword
    /// until a refresh says otherwise. False, with why, for an entry that is none of the three
    /// forms: a host name would have to be resolved, and whose word is taken for an address
    /// should not depend on DNS.
    /// </summary>
    public static bool TryParse(string? value, IReadOnlyList<IPNetwork> cloudflare, out TrustedProxies proxies, out string problem)
    {
        ArgumentNullException.ThrowIfNull(cloudflare);

        var named = new List<IPNetwork>();
        var followsCloudflare = false;
        problem = string.Empty;

        foreach (var entry in (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.Equals(entry, CloudflareKeyword, StringComparison.OrdinalIgnoreCase))
            {
                followsCloudflare = true;
            }
            else if (IPAddress.TryParse(entry, out var address))
            {
                named.Add(new IPNetwork(address, address.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32));
            }
            else if (IPNetwork.TryParse(entry, out var network) && network.PrefixLength > 0)
            {
                named.Add(network);
            }
            else
            {
                // A /0 parses, and would believe any header from anyone: the same as no check.
                problem = $"must be IP addresses, networks (10.0.0.0/24) or '{CloudflareKeyword}', separated by commas; got '{entry}'.";
                proxies = new TrustedProxies([], false);
                return false;
            }
        }

        proxies = new TrustedProxies(named, followsCloudflare);
        if (followsCloudflare)
        {
            proxies.ReplaceCloudflare(cloudflare);
        }

        return true;
    }
}
