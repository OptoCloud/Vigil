using Vigil.Core.Protocol;

namespace Vigil.Core.Tests;

/// <summary>
/// ZFS reports shaped like orion's <c>void</c>: a raidz2 of HDDs and two hot spares. Built by
/// hand, with only what a test changes passed in, so each test reads as the one thing it varies.
/// </summary>
internal static class PoolSamples
{
    public const string AsOf = "2026-10-08T04:47:00.000Z";

    public static readonly DateTimeOffset At = DateTimeOffset.Parse(AsOf, System.Globalization.CultureInfo.InvariantCulture);

    public static VdevStatus Disk(string name, string guid, string state = "ONLINE", ulong read = 0, ulong write = 0, ulong checksum = 0) => new()
    {
        Name = name,
        Guid = guid,
        Path = $"/dev/disk/by-id/wwn-{name}-part1",
        DeviceName = $"naa.{name}",
        Port = null,
        Parent = "raidz2-0",
        Class = "normal",
        State = state,
        Aux = state == "FAULTED" ? "ERR_EXCEEDED" : null,
        ReadErrors = read,
        WriteErrors = write,
        ChecksumErrors = checksum,
        SlowIos = 0,
    };

    public static VdevStatus Spare(string name, string guid, string state = "AVAIL") =>
        Disk(name, guid, state) with { Parent = null, Class = "spare" };

    public static PoolScan Scan(string function, string state, string start, string? end = null, ulong errors = 0) => new()
    {
        Function = function,
        State = state,
        StartTime = start,
        EndTime = end,
        ToExamineBytes = 1000,
        ExaminedBytes = state == "FINISHED" ? 1000UL : 400UL,
        IssuedBytes = state == "FINISHED" ? 1000UL : 400UL,
        ProcessedBytes = 0,
        Errors = errors,
    };

    public static PoolStatus Pool(
        string state = "ONLINE",
        PoolScan? scan = null,
        IReadOnlyList<VdevStatus>? vdevs = null,
        ulong allocated = 500,
        ulong errorCount = 0) => new()
        {
            Name = "void",
            State = state,
            ErrorCount = errorCount,
            SizeBytes = 1000,
            AllocatedBytes = allocated,
            Scan = scan,
            Vdevs = vdevs ?? [Disk("a", "1"), Disk("b", "2"), Spare("s1", "8"), Spare("s2", "9")],
        };

    public static ZfsStatus Zfs(PoolStatus pool, string asOf = AsOf) => new() { IsAvailable = true, AsOf = asOf, Pools = [pool] };

    public static ZfsStatus Unreadable(string asOf = AsOf) => new() { IsAvailable = false, AsOf = asOf, Pools = [] };
}
