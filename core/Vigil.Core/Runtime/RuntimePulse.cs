using Vigil.Core.Protocol;

namespace Vigil.Core.Runtime;

/// <summary>
/// The state the loop last held, as the references it held it by. Every one is an immutable
/// record or a list nothing writes to again, so another thread may read them as they are.
/// </summary>
public sealed record RuntimeView(Snapshot? Snapshot, IReadOnlyList<DriveState> Drives, UpsState? Ups, IReadOnlyList<Alert> Alerts, HostLoad? Host)
{
    public static RuntimeView Empty { get; } = new(null, [], null, [], null);
}

/// <summary>
/// What the runtime's loop last said about itself, readable from any thread. The loop owns all
/// state and takes no locks, so the two things outside it that ask "are you alive?" (the status
/// route and the heartbeat) read this one immutable value instead of reaching in.
/// </summary>
public sealed class RuntimePulse(TimeProvider time)
{
    /// <param name="At">The clock's timestamp when the loop last finished a turn.</param>
    private sealed record Beat(long At, bool DaemonConnected, bool DatabaseReady, RuntimeView View);

    private volatile Beat? _latest;

    /// <summary>The loop's own call, once a turn.</summary>
    internal void Record(bool daemonConnected, bool databaseReady, RuntimeView view) =>
        _latest = new Beat(time.GetTimestamp(), daemonConnected, databaseReady, view);

    /// <summary>How long ago the loop last turned. Null before its first turn.</summary>
    public TimeSpan? LoopAge => _latest is { } beat ? time.GetElapsedTime(beat.At) : null;

    public bool DaemonConnected => _latest?.DaemonConnected ?? false;

    public bool DatabaseReady => _latest?.DatabaseReady ?? false;

    /// <summary>When vigild took its latest snapshot, by vigild's clock. Null before the first.</summary>
    public string? LastSnapshot => _latest?.View.Snapshot?.TimestampUtc;

    /// <summary>What the loop held when it last turned, for <c>/metrics</c>.</summary>
    public RuntimeView View => _latest?.View ?? RuntimeView.Empty;

    /// <summary>
    /// Whether the loop has turned within <paramref name="within"/>. It turns every second when
    /// idle; only a database stall holds it, and that is bounded by the statement timeout.
    /// </summary>
    public bool IsTurning(TimeSpan within) => LoopAge is { } age && age <= within;
}
