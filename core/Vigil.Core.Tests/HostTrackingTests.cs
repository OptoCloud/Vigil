using Vigil.Core.Protocol;
using Vigil.Core.Runtime;
using Xunit;

namespace Vigil.Core.Tests;

public sealed class HostTrackingTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    private const ulong Gib = 1UL << 30;

    /// <summary>
    /// A host <paramref name="seconds"/> after <see cref="Start"/> that has done
    /// <paramref name="work"/> units of everything per second since boot: each counter is a
    /// multiple of it, so every rate between two of these is known in advance.
    /// </summary>
    private static HostStatus Host(double seconds, ulong work = 1, double uptime = 1_000, IReadOnlyList<DiskCounters>? disks = null) =>
        new()
        {
            AsOf = Rfc3339.From(Start.AddSeconds(seconds)),
            UptimeSeconds = uptime + seconds,
            Memory = new MemoryStatus
            {
                TotalBytes = 64 * Gib,
                AvailableBytes = 20 * Gib,
                FreeBytes = 2 * Gib,
                BuffersBytes = 0,
                CachedBytes = 6 * Gib,
                SwapTotalBytes = 8 * Gib,
                SwapFreeBytes = 7 * Gib,
                ArcBytes = 32 * Gib,
                ArcMaxBytes = 32 * Gib,
            },
            // Per second of work: 20 ticks busy, 5 waiting on I/O, 75 idle, out of 100.
            Cpu = Ticks((ulong)(seconds * work)),
            Load = new LoadAverage { One = 1.5, Five = 1, Fifteen = 0.5 },
            Pressure = new Pressure
            {
                Cpu = new PressureStall { SomeMicros = (ulong)(seconds * 10_000), FullMicros = null },
                Memory = null,
                Io = new PressureStall { SomeMicros = (ulong)(seconds * 250_000), FullMicros = 0 },
            },
            Disks = disks ?? [Disk("naa.1", seconds), Disk("naa.2", seconds), Disk("nvme0n1", seconds)],
            Interfaces = [new InterfaceCounters { Name = "vmbr0", ReceivedBytes = (ulong)(seconds * 1_000_000), TransmittedBytes = (ulong)(seconds * 200_000) }],
        };

    private static CpuTicks Ticks(ulong units) => new()
    {
        User = 15 * units,
        Nice = 0,
        System = 4 * units,
        Idle = 75 * units,
        Iowait = 5 * units,
        Irq = 0,
        Softirq = units,
        Steal = 0,
        LogicalCpus = 16,
    };

    /// <summary>10 MB/s read, 2 MB/s written, busy a quarter of the time.</summary>
    private static DiskCounters Disk(string name, double seconds) => new()
    {
        DeviceName = name,
        Port = null,
        ReadBytes = (ulong)(seconds * 10_000_000),
        WrittenBytes = (ulong)(seconds * 2_000_000),
        BusyMillis = (ulong)(seconds * 250),
    };

    private static readonly ZfsStatus Pools = new()
    {
        IsAvailable = true,
        AsOf = "t",
        Pools =
        [
            Pool("tank", "naa.1", "naa.2", "naa.2", null),
            Pool("rpool", "nvme0n1"),
            Pool("scratch", "naa.gone"),
        ],
    };

    private static PoolStatus Pool(string name, params string?[] disks) => new()
    {
        Name = name,
        State = "ONLINE",
        ErrorCount = 0,
        SizeBytes = 100,
        AllocatedBytes = 50,
        Scan = null,
        Vdevs =
        [
            .. disks.Select(disk => new VdevStatus
            {
                Name = disk ?? "/tmp/file",
                Guid = null,
                Path = null,
                DeviceName = disk,
                Port = null,
                Parent = null,
                Class = "normal",
                State = "ONLINE",
                Aux = null,
                ReadErrors = 0,
                WriteErrors = 0,
                ChecksumErrors = 0,
                SlowIos = 0,
            }),
        ],
    };

    private static HostLoad Between(HostStatus? before, HostStatus now, ZfsStatus? zfs = null) =>
        HostTracking.Between(before, now, zfs, HostTracking.LiveMaxInterval);

    [Fact]
    public void TwoReadingsBecomeRatesOverTheTimeBetweenThem()
    {
        var load = Between(Host(10), Host(12));

        Assert.Equal(2, load.IntervalSeconds);
        Assert.Equal(20, load.Cpu!.BusyPercent, 6);
        Assert.Equal(5, load.Cpu.IowaitPercent, 6);
        Assert.Equal(16u, load.Cpu.LogicalCpus);
        Assert.Equal(1, load.Pressure!.CpuPercent!.Value, 6);
        Assert.Equal(25, load.Pressure.IoPercent!.Value, 6);
        Assert.Null(load.Pressure.MemoryPercent);

        var disk = load.Disks[0];
        Assert.Equal((10_000_000d, 2_000_000d, 25d), (disk.ReadBytesPerSecond, disk.WrittenBytesPerSecond, disk.BusyPercent));
        var link = Assert.Single(load.Interfaces);
        Assert.Equal(("vmbr0", 1_000_000d, 200_000d), (link.Name, link.ReceivedBytesPerSecond, link.TransmittedBytesPerSecond));
    }

    [Fact]
    public void MemoryIsUsedAsFreeCountsItWithTheArcAlongside()
    {
        var memory = Between(null, Host(0)).Memory!;

        Assert.Equal(44 * Gib, memory.UsedBytes);
        Assert.Equal(1 * Gib, memory.SwapUsedBytes);
        Assert.Equal(32 * Gib, memory.ArcBytes);
        Assert.Equal(6 * Gib, memory.CachedBytes);
    }

    [Fact]
    public void TheFirstReadingHasItsGaugesButNoRates()
    {
        var load = Between(null, Host(0));

        Assert.Null(load.IntervalSeconds);
        Assert.Null(load.Cpu);
        Assert.Null(load.Pressure);
        Assert.Empty(load.Disks);
        Assert.Empty(load.Interfaces);
        Assert.NotNull(load.Memory);
        Assert.Equal(1.5, load.Load!.One);
    }

    [Fact]
    public void AReadingAfterARebootIsNoRateRatherThanANegativeOne()
    {
        // Uptime went down, so every counter started again: what looks like a huge negative
        // delta, or a small positive one by luck, is neither.
        var load = Between(Host(10, work: 1_000), Host(12, work: 1, uptime: 0));

        Assert.Null(load.IntervalSeconds);
        Assert.Null(load.Cpu);
        Assert.Empty(load.Disks);
    }

    [Fact]
    public void TooLongAGapOrNoTimeAtAllGivesNoRate()
    {
        Assert.Null(Between(Host(0), Host(61)).IntervalSeconds);
        Assert.Null(Between(Host(5), Host(5)).IntervalSeconds);
        Assert.Null(Between(Host(5), Host(4)).IntervalSeconds);
        Assert.Equal(60, Between(Host(0), Host(60)).IntervalSeconds);
    }

    [Fact]
    public void ADiskThatAppearedOrWentBackwardsIsLeftOutAndTheOthersKept()
    {
        var before = Host(10, disks: [Disk("naa.1", 10), Disk("naa.2", 500)]);
        var now = Host(12, disks: [Disk("naa.1", 12), Disk("naa.2", 12), Disk("naa.new", 12)]);

        var load = Between(before, now);

        Assert.Equal(["naa.1"], load.Disks.Select(disk => disk.DeviceName));
    }

    [Fact]
    public void APoolsTrafficIsTheSumOfItsDistinctDisksAndAPoolWithNoneIsLeftOut()
    {
        var load = Between(Host(10), Host(12), Pools);

        // tank's two disks, the second named twice (a mirror of partitions on one disk) and a file vdev ignored.
        Assert.Equal(["tank", "rpool"], load.Pools.Select(pool => pool.Name));
        Assert.Equal((20_000_000d, 4_000_000d), (load.Pools[0].ReadBytesPerSecond, load.Pools[0].WrittenBytesPerSecond));
        Assert.Equal(10_000_000, load.Pools[1].ReadBytesPerSecond);
    }

    [Fact]
    public void ABusyCounterThatOutrunsTheClockReadsAsAFullDiskNotMore()
    {
        var before = Host(10, disks: [Disk("naa.1", 10) with { BusyMillis = 0 }]);
        var now = Host(12, disks: [Disk("naa.1", 12) with { BusyMillis = 2_010 }]);

        Assert.Equal(100, Between(before, now).Disks[0].BusyPercent);
    }

    [Fact]
    public void TheLiveRateFollowsEachReadingAndHistoryAveragesSinceItsLastRow()
    {
        var tracking = new HostTracking(TimeSpan.FromSeconds(10));

        Assert.Null(tracking.TakeSample(null));
        Assert.NotNull(tracking.Observe(Host(0), null));
        // The same reading repeated in the next snapshot is not new.
        Assert.Null(tracking.Observe(Host(0), null));
        Assert.Null(tracking.Observe(null, null));

        // The first row has no rate, only what one reading can say.
        var first = tracking.TakeSample(null)!;
        Assert.Null(first.Cpu);

        // Ten seconds: four seconds of no work at all, then six of a disk at full tilt.
        tracking.Observe(Host(4, disks: [Disk("naa.1", 0)]), null);
        tracking.Observe(Host(10, disks: [Disk("naa.1", 0) with { ReadBytes = 60_000_000, BusyMillis = 6_000 }]), null);
        Assert.Equal(10_000_000, tracking.Latest!.Disks.Single().ReadBytesPerSecond);

        var sample = tracking.TakeSample(null)!;
        Assert.Equal(10, sample.IntervalSeconds);
        Assert.Equal(6_000_000, Assert.Single(sample.Disks).ReadBytesPerSecond);
        Assert.Equal(60, sample.Disks[0].BusyPercent);
    }
}
