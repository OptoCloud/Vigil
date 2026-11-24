namespace Vigil.Core.Protocol;

/// <summary>
/// What <c>GET /api/status</c> answers with: whether vigil-core is doing its job, for a monitor
/// rather than a person. Published like everything else (ADR-023), so it says only what the
/// dashboard already shows, and which release this is, which the repository's tags say too.
/// </summary>
public sealed record StatusResponse
{
    /// <summary>
    /// True when the runtime's loop is turning and vigild is connected: the two things without
    /// which the page shows nothing current. The route answers 503 when it is false.
    /// </summary>
    public required bool Ready { get; init; }

    /// <summary>The release tag this was built for, or "development".</summary>
    public required string Version { get; init; }

    public required bool DaemonConnected { get; init; }

    /// <summary>When vigild took its latest snapshot (RFC 3339, UTC). Null before the first.</summary>
    public required string? LastSnapshot { get; init; }

    /// <summary>False while the database is down: history is not being written, the live view is unaffected.</summary>
    public required bool DatabaseReady { get; init; }

    /// <summary>Seconds since the runtime's loop last turned. Null before its first turn.</summary>
    public required double? LoopAgeSeconds { get; init; }
}
