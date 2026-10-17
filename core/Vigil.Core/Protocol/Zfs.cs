using System.Diagnostics.CodeAnalysis;

namespace Vigil.Core.Protocol;

// vigild's ZFS pool state, one `zpool status` poll embedded in every snapshot (ADR-018). Mirrors
// daemon/src/protocol/mod.rs and is checked against daemon/contract.json like the rest of the
// snapshot. States are zpool's own words, as text: a state a later ZFS adds must arrive as "not
// ONLINE", not make the snapshot unreadable.

public sealed record ZfsStatus
{
    /// <summary>
    /// False when zpool could not be run, timed out, or printed something that is not its JSON.
    /// <see cref="Pools"/> is then empty, and that means "unknown", never "no pools".
    /// </summary>
    public required bool IsAvailable { get; init; }

    /// <summary>When this poll ran (RFC 3339, UTC). Pools are polled far slower than the snapshot.</summary>
    public required string AsOf { get; init; }

    public required IReadOnlyList<PoolStatus> Pools { get; init; }
}

public sealed record PoolStatus
{
    public required string Name { get; init; }

    /// <summary>ONLINE, DEGRADED, FAULTED, OFFLINE, REMOVED, UNAVAIL, SUSPENDED, or UNKNOWN when zpool gave none.</summary>
    public required string State { get; init; }

    /// <summary>Permanent data errors: blocks ZFS could not repair from redundancy.</summary>
    public required ulong? ErrorCount { get; init; }

    /// <summary>Raw space across every disk, parity included, as <c>zpool list</c> counts it.</summary>
    public required ulong? SizeBytes { get; init; }

    public required ulong? AllocatedBytes { get; init; }

    /// <summary>The latest scrub or resilver, whichever ran last. Null when the pool was never scanned.</summary>
    public required PoolScan? Scan { get; init; }

    /// <summary>Every leaf vdev, hot spares included.</summary>
    public required IReadOnlyList<VdevStatus> Vdevs { get; init; }
}

public sealed record PoolScan
{
    /// <summary>SCRUB, RESILVER or ERRORSCRUB.</summary>
    public required string Function { get; init; }

    /// <summary>SCANNING, FINISHED or CANCELED.</summary>
    public required string State { get; init; }

    public required string? StartTime { get; init; }

    /// <summary>Null while the scan is running.</summary>
    public required string? EndTime { get; init; }

    public required ulong? ToExamineBytes { get; init; }
    public required ulong? ExaminedBytes { get; init; }

    /// <summary>What zpool's own progress figure counts.</summary>
    public required ulong? IssuedBytes { get; init; }

    /// <summary>Repaired by a scrub, or written by a resilver.</summary>
    public required ulong? ProcessedBytes { get; init; }

    public required ulong? Errors { get; init; }
}

public sealed record VdevStatus
{
    public required string Name { get; init; }

    /// <summary>Text, because a u64 outgrows the numbers a browser can hold exactly.</summary>
    [SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "The wire name is zpool's own \"guid\", which the naming policy derives from this property.")]
    public required string? Guid { get; init; }

    public required string? Path { get; init; }

    /// <summary>
    /// The disk it is on, named as <see cref="DriveHealth.DeviceName"/> names it, so ZFS and SMART
    /// for one disk can be joined. Null for a vdev that is not on a disk.
    /// </summary>
    public required string? DeviceName { get; init; }

    public required string? Port { get; init; }

    /// <summary>The vdev it sits in ("raidz2-0", "spare-5"), or null at the top of the pool.</summary>
    public required string? Parent { get; init; }

    /// <summary>normal, special, dedup, log, l2cache or spare.</summary>
    public required string Class { get; init; }

    /// <summary>ONLINE, DEGRADED, FAULTED, OFFLINE, REMOVED, UNAVAIL; for a hot spare, AVAIL or INUSE.</summary>
    public required string State { get; init; }

    /// <summary>Why it is not ONLINE, in zpool's words: ERR_EXCEEDED is "faulted for too many errors".</summary>
    public required string? Aux { get; init; }

    public required ulong? ReadErrors { get; init; }
    public required ulong? WriteErrors { get; init; }
    public required ulong? ChecksumErrors { get; init; }
    public required ulong? SlowIos { get; init; }
}
