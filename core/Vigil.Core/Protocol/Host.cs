namespace Vigil.Core.Protocol;

// The host's load as vigild sends it: daemon/src/protocol/host.rs, pinned in
// daemon/contract.json and checked by DaemonContractTests (ADR-025). Growing quantities are the
// kernel's cumulative counters; HostTracking turns two of them into a rate.

/// <summary>One reading of the host's <c>/proc</c>. Each part is null on its own when unreadable.</summary>
public sealed record HostStatus
{
    /// <summary>When <c>/proc</c> was read. Rates are taken between two of these, not between snapshots.</summary>
    public required string AsOf { get; init; }

    /// <summary>Smaller than the previous reading means the host rebooted and every counter restarted.</summary>
    public required double? UptimeSeconds { get; init; }

    public required MemoryStatus? Memory { get; init; }
    public required CpuTicks? Cpu { get; init; }
    public required LoadAverage? Load { get; init; }
    public required Pressure? Pressure { get; init; }

    /// <summary>Whole disks backed by hardware; zvols and partitions are left out by vigild.</summary>
    public required IReadOnlyList<DiskCounters> Disks { get; init; }

    /// <summary>Only the interfaces vigild's <c>[host] interfaces</c> names.</summary>
    public required IReadOnlyList<InterfaceCounters> Interfaces { get; init; }
}

public sealed record MemoryStatus
{
    public required ulong TotalBytes { get; init; }

    /// <summary>MemAvailable. Total minus this is "used", as <c>free</c> counts it.</summary>
    public required ulong AvailableBytes { get; init; }

    public required ulong FreeBytes { get; init; }
    public required ulong BuffersBytes { get; init; }

    /// <summary>Cached plus SReclaimable.</summary>
    public required ulong CachedBytes { get; init; }

    public required ulong SwapTotalBytes { get; init; }
    public required ulong SwapFreeBytes { get; init; }

    /// <summary>Null without ZFS. Linux counts the ARC as used, not as cache.</summary>
    public required ulong? ArcBytes { get; init; }

    public required ulong? ArcMaxBytes { get; init; }
}

/// <summary>Ticks since boot summed over every CPU. Only ratios of differences mean anything.</summary>
public sealed record CpuTicks
{
    public required ulong User { get; init; }
    public required ulong Nice { get; init; }
    public required ulong System { get; init; }
    public required ulong Idle { get; init; }
    public required ulong Iowait { get; init; }
    public required ulong Irq { get; init; }
    public required ulong Softirq { get; init; }
    public required ulong Steal { get; init; }
    public required uint LogicalCpus { get; init; }
}

public sealed record LoadAverage
{
    public required double One { get; init; }
    public required double Five { get; init; }
    public required double Fifteen { get; init; }
}

/// <summary>PSI. Each is null when the kernel has no PSI.</summary>
public sealed record Pressure
{
    public required PressureStall? Cpu { get; init; }
    public required PressureStall? Memory { get; init; }
    public required PressureStall? Io { get; init; }
}

/// <summary>Cumulative microseconds stalled since boot.</summary>
public sealed record PressureStall
{
    public required ulong SomeMicros { get; init; }
    public required ulong? FullMicros { get; init; }
}

/// <summary>One whole disk, named as <see cref="DriveHealth.DeviceName"/> names it.</summary>
public sealed record DiskCounters
{
    public required string DeviceName { get; init; }
    public required string? Port { get; init; }
    public required ulong ReadBytes { get; init; }
    public required ulong WrittenBytes { get; init; }

    /// <summary>Milliseconds with I/O in flight; its rate is <c>iostat</c>'s %util.</summary>
    public required ulong BusyMillis { get; init; }
}

public sealed record InterfaceCounters
{
    public required string Name { get; init; }
    public required ulong ReceivedBytes { get; init; }
    public required ulong TransmittedBytes { get; init; }
}
