using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Vigil.Core.Clients;
using Vigil.Core.Configuration;
using IPNetwork = System.Net.IPNetwork;

namespace Vigil.Core.Api;

/// <summary>
/// Who a request is from, and how much any one of them may ask (docs/SECURITY.md §4.2).
/// </summary>
/// <remarks>
/// The dashboard is public and has no accounts (ADR-023), so the only identity a request has
/// is its address. That is enough to stop one machine in a loop from using what everyone
/// shares; it is not enough to stop many machines, which is a job for whatever sits in front.
/// ASP.NET's rate limiter and forwarded-headers middleware do the work (docs/STYLE.md §1.1);
/// what is ours is the limits and deciding whose word to take for an address.
/// </remarks>
public static class Admission
{
    /// <summary>The limit every route that queries the database is under.</summary>
    public const string DatabasePolicy = "database";

    /// <summary>An IPv6 customer is handed a /64 at the least, so that is what one client is.</summary>
    private const int Ipv6ClientPrefixBytes = 8;

    private const string TooFast = "Too many requests. Slow down and try again shortly.\n";
    private const string Busy = "Too many history requests are in progress. Try again shortly.\n";

    /// <summary>
    /// The client a request counts against: its address, after <see cref="UseForwardedClient"/>
    /// has replaced a trusted proxy's with the one the proxy reports.
    /// </summary>
    public static string ClientKey(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return ClientKey(context.Connection.RemoteIpAddress);
    }

    internal static string ClientKey(IPAddress? address)
    {
        if (address is null)
        {
            return "unknown";
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, Ipv6ClientPrefixBytes, bytes.Length - Ipv6ClientPrefixBytes);
        return $"{new IPAddress(bytes)}/64";
    }

    /// <summary>
    /// The per-client request allowance on every request, and the cap on database queries on
    /// the routes that make one: nothing here is authenticated, and without the second each
    /// request holds a pooled connection for up to the statement timeout while the rest queue
    /// behind the pool. Needs <c>UseRateLimiter</c>, which ASP.NET refuses to route without.
    /// Also the trusted proxies, and the refresh of Cloudflare's when they are named.
    /// </summary>
    public static IServiceCollection AddAdmission(this IServiceCollection services, VigilOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Validation has the say on a bad value (it runs at start and lists every problem);
        // here one only means nobody is trusted.
        TrustedProxies.TryParse(options.TrustedProxies, CloudflareRanges.BuiltIn, out var proxies, out _);
        services.AddSingleton(proxies);
        if (proxies.FollowsCloudflare)
        {
            services.AddSingleton(_ => new CloudflareRanges(new HttpClient(new SocketsHttpHandler
            {
                ConnectTimeout = CloudflareRanges.FetchTimeout,
                MaxResponseHeadersLength = CloudflareRanges.MaximumListBytes / 1024,

                // The lists are served where they are asked for; a redirect is not followed
                // to wherever it points.
                AllowAutoRedirect = false,
                PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            })));
            services.AddHostedService<CloudflareRangesRefresh>();
        }

        return services.AddRateLimiter(limiter =>
        {
            limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                RateLimitPartition.GetTokenBucketLimiter(ClientKey(context), _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = Limits.ClientRequestBurst,
                    TokensPerPeriod = Limits.ClientRequestsPerSecond,
                    ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                    QueueLimit = 0,
                }));

            limiter.AddConcurrencyLimiter(DatabasePolicy, limit =>
            {
                limit.PermitLimit = Limits.DatabaseQueryConcurrency;
                limit.QueueLimit = Limits.DatabaseQueryQueue;
                limit.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
            });

            limiter.RejectionStatusCode = StatusCodes.Status503ServiceUnavailable;
            limiter.OnRejected = (rejected, cancellationToken) =>
            {
                var response = rejected.HttpContext.Response;
                response.ContentType = "text/plain; charset=utf-8";

                // Only the client's own allowance says when to come back; a busy database does not.
                if (!rejected.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    return new ValueTask(response.WriteAsync(Busy, cancellationToken));
                }

                response.StatusCode = StatusCodes.Status429TooManyRequests;
                response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                return new ValueTask(response.WriteAsync(TooFast, cancellationToken));
            };
        });
    }

    /// <summary>
    /// Takes a visitor's address and scheme from <c>X-Forwarded-For</c> and
    /// <c>X-Forwarded-Proto</c>, but only on a connection from one of
    /// <paramref name="proxies"/>, and only the one hop that proxy added. From anyone else the
    /// headers are a claim, and believing it would let a client pick a new address for every
    /// request. First in the pipeline: everything after it counts by address.
    /// </summary>
    /// <remarks>
    /// Cloudflare needs nothing of its own here: it appends the visitor to
    /// <c>X-Forwarded-For</c> like any proxy, so the last hop is the address its
    /// <c>CF-Connecting-IP</c> carries. ASP.NET's middleware does the work; it reads its
    /// options once, so a new one is built whenever the trusted set is replaced.
    /// </remarks>
    public static IApplicationBuilder UseForwardedClient(this WebApplication app, TrustedProxies proxies)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(proxies);

        if (proxies.Current.Count == 0)
        {
            return app.Use(WarnOnceAboutAnUnnamedProxy(app.Logger));
        }

        var logging = app.Services.GetRequiredService<ILoggerFactory>();
        return app.Use(next =>
        {
            var built = Build(proxies.Current, next, logging);
            return context =>
            {
                var current = proxies.Current;
                var held = Volatile.Read(ref built);
                if (!ReferenceEquals(held.Trusted, current))
                {
                    held = Build(current, next, logging);
                    Volatile.Write(ref built, held);
                }

                return held.Middleware.Invoke(context);
            };
        });
    }

    private sealed record Forwarding(IReadOnlyList<IPNetwork> Trusted, ForwardedHeadersMiddleware Middleware);

    private static Forwarding Build(IReadOnlyList<IPNetwork> trusted, RequestDelegate next, ILoggerFactory logging)
    {
        var forwarded = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            ForwardLimit = 1,
        };

        // The defaults trust loopback, which is right only if the operator said so.
        forwarded.KnownProxies.Clear();
        forwarded.KnownIPNetworks.Clear();
        foreach (var network in trusted)
        {
            forwarded.KnownIPNetworks.Add(network);
        }

        return new Forwarding(trusted, new ForwardedHeadersMiddleware(next, logging, Microsoft.Extensions.Options.Options.Create(forwarded)));
    }

    /// <summary>
    /// With no proxy named, a request that carries <c>X-Forwarded-For</c> most likely came
    /// through one, and every visitor is then being counted as that one address: eight live
    /// streams for the whole internet. Said once, with the address to put in the setting.
    /// </summary>
    private static Func<HttpContext, RequestDelegate, Task> WarnOnceAboutAnUnnamedProxy(ILogger logger)
    {
        var warned = 0;
        return (context, next) =>
        {
            if (context.Request.Headers.ContainsKey("X-Forwarded-For") && Interlocked.Exchange(ref warned, 1) == 0)
            {
                logger.LogWarning(
                    "a request from {Peer} carries X-Forwarded-For, and TRUSTED_PROXIES is not set. If {Peer} is your reverse proxy, "
                    + "set TRUSTED_PROXIES to it (or to 'cloudflare'): until then every visitor shares that one address's limits.",
                    context.Connection.RemoteIpAddress,
                    context.Connection.RemoteIpAddress);
            }

            return next(context);
        };
    }
}
