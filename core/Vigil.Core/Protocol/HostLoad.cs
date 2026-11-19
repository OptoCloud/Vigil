namespace Vigil.Core.Protocol;

// The host's load as the page sees it: vigild's counters turned into rates by
// Runtime.HostTracking (ADR-025). Part of the live stream, so pinned in core/contract.json.

/// <summary>
/// The host's load at one reading. A rate is null when there is nothing to take it against: the
/// first reading, a reboot, a counter that went backwards, or too long a gap. Unknown is never
/// drawn as zero.
/// </summary>
public sealed record HostLoad
{
    /// <summary>When vigild read <c>/proc</c> for the later of the two readings.</summary>
    public required string AsOf { get; init; }

    /// <summary>The seconds the rates were taken over; null when there are none.</summary>
    public required double? IntervalSeconds { get; init; }

    public required double? UptimeSeconds { get; init; }
    public required MemoryLoad? Memory { get; init; }
    public required CpuLoad? Cpu { get; init; }
    public required LoadAverage? Load { get; init; }
    public required PressureLoad? Pressure { get; init; }
    public required IReadOnlyList<DiskLoad> Disks { get; init; }

    /// <summary>Each pool's traffic, the sum of the disks its vdevs are on.</summary>
    public required IReadOnlyList<PoolLoad> Pools { get; init; }

    public required IReadOnlyList<InterfaceLoad> Interfaces { get; init; }
}

public sealed record MemoryLoad
{
    public required ulong TotalBytes { get; init; }

    /// <summary>Total minus MemAvailable, as <c>free</c> counts "used". Includes the ARC.</summary>
    public required ulong UsedBytes { get; init; }

    public required ulong CachedBytes { get; init; }
    public required ulong? ArcBytes { get; init; }
    public required ulong? ArcMaxBytes { get; init; }
    public required ulong SwapTotalBytes { get; init; }
    public required ulong SwapUsedBytes { get; init; }
}

public sealed record CpuLoad
{
    /// <summary>Percent of every CPU's time spent on anything but idling and waiting on I/O.</summary>
    public required double BusyPercent { get; init; }

    /// <summary>Percent idling while I/O was outstanding: the disks held the host up.</summary>
    public required double IowaitPercent { get; init; }

    public required uint LogicalCpus { get; init; }
}

/// <summary>
/// PSI, as percent of the time. "Some": at least one task was held up waiting. "Full": every
/// runnable task was, so the host as a whole stood still; null where the kernel does not count it.
/// </summary>
public sealed record PressureLoad
{
    public required double? CpuPercent { get; init; }
    public required double? MemoryPercent { get; init; }
    public required double? IoPercent { get; init; }
    public required double? CpuFullPercent { get; init; }
    public required double? MemoryFullPercent { get; init; }
    public required double? IoFullPercent { get; init; }
}

public sealed record DiskLoad
{
    /// <summary>As <see cref="DriveHealth.DeviceName"/> names the disk.</summary>
    public required string DeviceName { get; init; }

    public required string? Port { get; init; }
    public required double ReadBytesPerSecond { get; init; }
    public required double WrittenBytesPerSecond { get; init; }

    /// <summary>Percent of the time with I/O in flight, <c>iostat</c>'s %util.</summary>
    public required double BusyPercent { get; init; }
}

public sealed record PoolLoad
{
    public required string Name { get; init; }
    public required double ReadBytesPerSecond { get; init; }
    public required double WrittenBytesPerSecond { get; init; }
}

public sealed record InterfaceLoad
{
    public required string Name { get; init; }
    public required double ReceivedBytesPerSecond { get; init; }
    public required double TransmittedBytesPerSecond { get; init; }
}
