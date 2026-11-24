using System.Data.Common;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Vigil.Core.Configuration;
using Vigil.Core.Data;
using Vigil.Core.Protocol;
using Vigil.Core.Runtime;

namespace Vigil.Core.Api;

/// <summary>
/// vigil-core's HTTP API. Every route is a GET: vigil has no mutating route, which is what
/// the public, unauthenticated dashboard depends on (ADR-023). Adding one voids that decision.
/// </summary>
public static class VigilApi
{
    /// <summary>A page of the event log when the request does not say: what a page shows on first paint.</summary>
    private const int DefaultEventCount = 50;

    /// <summary>The most events one request is sent. A deeper look is another page, not a bigger one.</summary>
    private const int MaxEventCount = 200;

    /// <summary>2020-01-01: before any vigil recorded anything, so an earlier <c>to</c> is a mistake, such as seconds for milliseconds.</summary>
    private const long EarliestHistoryMillis = 1_577_836_800_000;

    /// <summary>The severities <c>/api/events</c> can be narrowed to, by their wire names.</summary>
    private static readonly Dictionary<string, Severity> SeverityNames = Enum.GetValues<Severity>()
        .ToDictionary(severity => JsonSerializer.Serialize(severity, VigilJsonContext.Default.Severity).Trim('"'), StringComparer.Ordinal);

    private static readonly string EventsProblem =
        $"limit must be 1 to {MaxEventCount}, before an event's id, and severity one of {string.Join(", ", SeverityNames.Keys)}\n";

    /// <summary>
    /// The most of one drive's health changes a page is sent. A row is only written when a
    /// count moved, so a healthy disk has a handful in its life; this bounds a failing one.
    /// </summary>
    private const int DriveHistoryRows = 200;

    /// <summary>
    /// The longest drive name looked up. A WWN is about 20 characters and a t10 wwid, with its
    /// space padding, under 100; anything longer cannot be a disk vigild named.
    /// </summary>
    private const int MaxDriveNameLength = 128;

    /// <summary>Range names from the enum's own wire spelling, so there is one list of them.</summary>
    private static readonly Dictionary<string, RangeKey> RangeNames = Enum.GetValues<RangeKey>()
        .ToDictionary(range => JsonSerializer.Serialize(range, VigilJsonContext.Default.RangeKey).Trim('"'), StringComparer.Ordinal);

    private static readonly string RangeProblem = $"range must be one of {string.Join(", ", RangeNames.Keys)}\n";

    public static IEndpointRouteBuilder MapVigilApi(this IEndpointRouteBuilder app)
    {
        // 200 once running. Deliberately says nothing about the database: the live view has to
        // work without one (ARCHITECTURE.md invariant 5), so a database outage is not unhealthy.
        app.MapGet("/health", () => TypedResults.Text("ok\n"));

        app.MapGet("/api/status", Status);
        app.MapGet("/metrics", (RuntimePulse pulse) => TypedResults.Text(Metrics.Render(pulse), Metrics.ContentType));
        app.MapGet("/api/live", LiveEndpoint.Handle);
        app.MapGet("/api/history", HistoryAsync).RequireRateLimiting(Admission.DatabasePolicy);
        app.MapGet("/api/events", EventsAsync).RequireRateLimiting(Admission.DatabasePolicy);
        app.MapGet("/api/drives/{wwn}/history", DriveHistoryAsync).RequireRateLimiting(Admission.DatabasePolicy);

        // An unknown API path is a 404 with a hint, not the dashboard's index page.
        app.MapGet("/api/{**rest}", () => TypedResults.Text("try /api/live, /api/status, /api/history?range=24h, /api/events or /api/drives/<wwn>/history\n", statusCode: 404));
        return app;
    }

    /// <summary>
    /// The release this binary was built for: release.yml passes the tag as VIGIL_VERSION, which
    /// the project file makes the assembly's informational version. Anything else is a build
    /// from a working tree.
    /// </summary>
    internal static readonly string Version =
        typeof(VigilApi).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion is { } version
        && version.StartsWith('v')
            ? version
            : "development";

    /// <summary>
    /// Readiness, where /health is liveness: 200 while the loop turns and vigild is connected,
    /// 503 otherwise, with the same body either way. Reads only <see cref="RuntimePulse"/>, so
    /// it costs nothing and cannot be held up by whatever is wrong.
    /// </summary>
    private static Microsoft.AspNetCore.Http.HttpResults.JsonHttpResult<StatusResponse> Status(RuntimePulse pulse)
    {
        var status = new StatusResponse
        {
            Ready = pulse.IsTurning(Limits.LoopStalledAfter) && pulse.DaemonConnected,
            Version = Version,
            DaemonConnected = pulse.DaemonConnected,
            LastSnapshot = pulse.LastSnapshot,
            DatabaseReady = pulse.DatabaseReady,
            LoopAgeSeconds = pulse.LoopAge?.TotalSeconds,
        };
        return TypedResults.Json(status, VigilJsonContext.Default.StatusResponse, statusCode: status.Ready ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
    }

    private static async Task<IResult> HistoryAsync(
        string? range,
        long? to,
        HistoryQueries history,
        IOptions<VigilOptions> options,
        ILoggerFactory logging,
        CancellationToken cancellationToken)
    {
        if (range is null || !RangeNames.TryGetValue(range, out var key))
        {
            return TypedResults.Text(RangeProblem, statusCode: StatusCodes.Status400BadRequest);
        }

        // The end of the window, in epoch milliseconds, for a look further back. Not in the
        // future (a clock a minute apart is allowed for), and not before vigil could exist.
        var now = DateTimeOffset.UtcNow;
        if (to is < EarliestHistoryMillis || to > now.AddMinutes(1).ToUnixTimeMilliseconds())
        {
            return TypedResults.Text("to must be a time in the past, in milliseconds since 1970\n", statusCode: StatusCodes.Status400BadRequest);
        }

        var ups = options.Value.NutHost is { Length: > 0 } ? options.Value.NutUps : null;
        try
        {
            var until = to is { } millis ? DateTimeOffset.FromUnixTimeMilliseconds(millis) : (DateTimeOffset?)null;
            var response = await history
                .QueryAsync(key, ups, now, cancellationToken, until, TimeSpan.FromDays(options.Value.RawRetentionDays))
                .ConfigureAwait(false);
            return TypedResults.Json(response, VigilJsonContext.Default.HistoryResponse);
        }
        catch (Exception error) when (error is DbException or TimeoutException)
        {
            logging.CreateLogger(typeof(VigilApi)).LogError("history query failed: {Error}", error.Message);
            return TypedResults.Text("History is unavailable: the database could not be reached.\n", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    /// <summary>
    /// One drive's recorded health changes, for its own page. The name is bound, never spliced,
    /// so the check here is only that it could be a name at all: the page asks for whatever is
    /// in its URL, and an unknown drive is an empty list.
    /// </summary>
    private static async Task<IResult> DriveHistoryAsync(string wwn, DriveStore drives, ILoggerFactory logging, CancellationToken cancellationToken)
    {
        if (wwn.Length > MaxDriveNameLength || wwn.Any(char.IsControl))
        {
            return TypedResults.Text("not a drive name\n", statusCode: StatusCodes.Status400BadRequest);
        }

        try
        {
            return TypedResults.Json(await drives.HistoryAsync(wwn, DriveHistoryRows, cancellationToken).ConfigureAwait(false), VigilJsonContext.Default.ListDriveHealthRecord);
        }
        catch (Exception error) when (error is DbException or TimeoutException)
        {
            logging.CreateLogger(typeof(VigilApi)).LogError("drive history query failed: {Error}", error.Message);
            return TypedResults.Text("Drive history is unavailable: the database could not be reached.\n", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    /// <summary>
    /// A page of the event log, newest first: the newest when nothing is asked, older ones with
    /// <c>before=&lt;the oldest id already shown&gt;</c>, narrowed with <c>severity=warning</c> or
    /// <c>critical</c> to that and worse. An empty list when the database is down, since the
    /// live stream still works and still carries what happens next.
    /// </summary>
    private static async Task<IResult> EventsAsync(int? limit, long? before, string? severity, EventStore events, ILoggerFactory logging, CancellationToken cancellationToken)
    {
        var minimum = Severity.Info;
        if (limit is < 1 or > MaxEventCount || before is < 1 || (severity is not null && !SeverityNames.TryGetValue(severity, out minimum)))
        {
            return TypedResults.Text(EventsProblem, statusCode: StatusCodes.Status400BadRequest);
        }

        try
        {
            var page = await events.PageAsync(limit ?? DefaultEventCount, before, minimum, cancellationToken).ConfigureAwait(false);
            return TypedResults.Json(page, VigilJsonContext.Default.ListEventRecord);
        }
        catch (Exception error) when (error is DbException or TimeoutException)
        {
            logging.CreateLogger(typeof(VigilApi)).LogError("event log query failed: {Error}", error.Message);
            return TypedResults.Json(Array.Empty<EventRecord>(), VigilJsonContext.Default.EventRecordArray);
        }
    }
}
