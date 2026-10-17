using System.Data.Common;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Vigil.Core.Configuration;
using Vigil.Core.Data;
using Vigil.Core.Protocol;

namespace Vigil.Core.Api;

/// <summary>
/// vigil-core's HTTP API. Every route is a GET: vigil has no mutating route, which is what
/// ADR-008's unauthenticated LAN dashboard depends on. Adding one voids that decision.
/// </summary>
public static class VigilApi
{
    /// <summary>The event log a page shows on first paint, as vigil-web's server load did.</summary>
    private const int RecentEventCount = 50;

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
        .ToDictionary(range => JsonSerializer.Serialize(range, VigilJson.Options).Trim('"'), StringComparer.Ordinal);

    private static readonly string RangeProblem = $"range must be one of {string.Join(", ", RangeNames.Keys)}\n";

    public static IEndpointRouteBuilder MapVigilApi(this IEndpointRouteBuilder app)
    {
        // 200 once running. Deliberately says nothing about the database: the live view has to
        // work without one (ARCHITECTURE.md invariant 5), so a database outage is not unhealthy.
        app.MapGet("/health", () => TypedResults.Text("ok\n"));

        app.MapGet("/api/live", LiveEndpoint.Handle);
        app.MapGet("/api/history", HistoryAsync);
        app.MapGet("/api/events", EventsAsync);
        app.MapGet("/api/drives/{wwn}/history", DriveHistoryAsync);

        // An unknown API path is a 404 with a hint, not the dashboard's index page.
        app.MapGet("/api/{**rest}", () => TypedResults.Text("try /api/live, /api/history?range=24h, /api/events or /api/drives/<wwn>/history\n", statusCode: 404));
        return app;
    }

    private static async Task<IResult> HistoryAsync(
        string? range,
        HistoryQueries history,
        IOptions<VigilOptions> options,
        ILoggerFactory logging,
        CancellationToken cancellationToken)
    {
        if (range is null || !RangeNames.TryGetValue(range, out var key))
        {
            return TypedResults.Text(RangeProblem, statusCode: StatusCodes.Status400BadRequest);
        }

        var ups = options.Value.NutHost is { Length: > 0 } ? options.Value.NutUps : null;
        try
        {
            var response = await history.QueryAsync(key, ups, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
            return TypedResults.Json(response, VigilJson.Options);
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
            return TypedResults.Json(await drives.HistoryAsync(wwn, DriveHistoryRows, cancellationToken).ConfigureAwait(false), VigilJson.Options);
        }
        catch (Exception error) when (error is DbException or TimeoutException)
        {
            logging.CreateLogger(typeof(VigilApi)).LogError("drive history query failed: {Error}", error.Message);
            return TypedResults.Text("Drive history is unavailable: the database could not be reached.\n", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    /// <summary>
    /// The newest events. The page is a static build now, so what vigil-web's server load used to
    /// hand it on first paint it fetches from here; an empty list when the database is down,
    /// as before, since the live stream still works.
    /// </summary>
    private static async Task<IResult> EventsAsync(EventStore events, ILoggerFactory logging, CancellationToken cancellationToken)
    {
        try
        {
            return TypedResults.Json(await events.RecentAsync(RecentEventCount, cancellationToken).ConfigureAwait(false), VigilJson.Options);
        }
        catch (Exception error) when (error is DbException or TimeoutException)
        {
            logging.CreateLogger(typeof(VigilApi)).LogError("event log query failed: {Error}", error.Message);
            return TypedResults.Json(Array.Empty<EventRecord>(), VigilJson.Options);
        }
    }
}
