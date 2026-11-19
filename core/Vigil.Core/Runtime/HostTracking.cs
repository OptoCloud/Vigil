using System.Globalization;
using Vigil.Core.Protocol;

namespace Vigil.Core.Runtime;

/// <summary>
/// The runtime's host-load state: vigild's previous reading, against which the next one becomes
/// a rate (ADR-025). Two baselines, because the page and history ask different questions: the
/// page wants the last couple of seconds, history the average over everything since the last
/// row it wrote, so that a burst between two rows is not lost. One owner, no locks.
/// </summary>
internal sealed class HostTracking(TimeSpan persistInterval)
{
    /// <summary>
    /// The longest a live rate may span. vigild reads the host every 2 s; past this, vigil-core
    /// has missed readings (vigild was unreachable) and an average over the gap, drawn as "now",
    /// would say the host is doing what it did on average while nobody watched.
    /// </summary>
    internal static readonly TimeSpan LiveMaxInterval = TimeSpan.FromSeconds(60);

    /// <summary>History's limit: two persist intervals, so one row written late still counts.</summary>
    private readonly TimeSpan _persistMaxInterval = TimeSpan.FromTicks(Math.Max(LiveMaxInterval.Ticks, 2 * persistInterval.Ticks));

    private HostStatus? _previous;
    private HostStatus? _persisted;

    /// <summary>The latest load, for the page and for a new subscriber. Null before the first reading.</summary>
    public HostLoad? Latest { get; private set; }

    /// <summary>Takes one snapshot's host reading. Returns the new load, or null when it is not a new reading.</summary>
    public HostLoad? Observe(HostStatus? host, ZfsStatus? zfs)
    {
        if (host is null || host.AsOf == _previous?.AsOf)
        {
            return null;
        }

        Latest = Between(_previous, host, zfs, LiveMaxInterval);
        _previous = host;
        return Latest;
    }

    /// <summary>The load since the last row written, which becomes the next one. Null before any reading.</summary>
    public HostLoad? TakeSample(ZfsStatus? zfs)
    {
        if (_previous is null)
        {
            return null;
        }

        var sample = Between(_persisted, _previous, zfs, _persistMaxInterval);
        _persisted = _previous;
        return sample;
    }

    /// <summary>
    /// The load at <paramref name="now"/>, with every rate taken against <paramref name="before"/>.
    /// Gauges come from <paramref name="now"/> alone, so they are there even when no rate is.
    /// </summary>
    internal static HostLoad Between(HostStatus? before, HostStatus now, ZfsStatus? zfs, TimeSpan maxInterval)
    {
        var seconds = IntervalSeconds(before, now, maxInterval);
        var earlier = seconds is null ? null : before;
        var disks = earlier is null ? [] : DiskRates(earlier, now, seconds!.Value);

        return new HostLoad
        {
            AsOf = now.AsOf,
            IntervalSeconds = seconds,
            UptimeSeconds = now.UptimeSeconds,
            Memory = now.Memory is { } memory ? Memory(memory) : null,
            Cpu = earlier?.Cpu is { } cpuBefore && now.Cpu is { } cpuNow ? Cpu(cpuBefore, cpuNow) : null,
            Load = now.Load,
            Pressure = earlier is null ? null : Pressure(earlier.Pressure, now.Pressure, seconds!.Value),
            Disks = disks,
            Pools = PoolRates(disks, zfs),
            Interfaces = earlier is null ? [] : InterfaceRates(earlier, now, seconds!.Value),
        };
    }

    /// <summary>
    /// Seconds between two readings, or null when no rate can be taken over them: there is no
    /// earlier one, time did not move forward, the gap is too long, or the host rebooted between
    /// them (its uptime went down), which started every counter again.
    /// </summary>
    private static double? IntervalSeconds(HostStatus? before, HostStatus now, TimeSpan maxInterval)
    {
        if (before is null || !TryParse(before.AsOf, out var from) || !TryParse(now.AsOf, out var to))
        {
            return null;
        }

        var interval = to - from;
        if (interval <= TimeSpan.Zero || interval > maxInterval)
        {
            return null;
        }

        if (before.UptimeSeconds is { } uptimeBefore && now.UptimeSeconds is { } uptimeNow && uptimeNow < uptimeBefore)
        {
            return null;
        }

        return interval.TotalSeconds;
    }

    private static bool TryParse(string text, out DateTimeOffset at) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out at);

    private static MemoryLoad Memory(MemoryStatus memory) => new()
    {
        TotalBytes = memory.TotalBytes,
        UsedBytes = memory.TotalBytes - Math.Min(memory.TotalBytes, memory.AvailableBytes),
        CachedBytes = memory.CachedBytes,
        ArcBytes = memory.ArcBytes,
        ArcMaxBytes = memory.ArcMaxBytes,
        SwapTotalBytes = memory.SwapTotalBytes,
        SwapUsedBytes = memory.SwapTotalBytes - Math.Min(memory.SwapTotalBytes, memory.SwapFreeBytes),
    };

    /// <summary>Null when any counter went backwards, or no tick passed at all.</summary>
    private static CpuLoad? Cpu(CpuTicks before, CpuTicks now)
    {
        ulong[] earlier = [before.User, before.Nice, before.System, before.Idle, before.Iowait, before.Irq, before.Softirq, before.Steal];
        ulong[] later = [now.User, now.Nice, now.System, now.Idle, now.Iowait, now.Irq, now.Softirq, now.Steal];
        if (earlier.Zip(later).Any(pair => pair.Second < pair.First))
        {
            return null;
        }

        double total = later.Zip(earlier).Sum(pair => (double)(pair.First - pair.Second));
        if (total <= 0)
        {
            return null;
        }

        double idle = now.Idle - before.Idle;
        double iowait = now.Iowait - before.Iowait;
        return new CpuLoad
        {
            BusyPercent = Percent((total - idle - iowait) / total),
            IowaitPercent = Percent(iowait / total),
            LogicalCpus = now.LogicalCpus,
        };
    }

    private static PressureLoad? Pressure(Pressure? before, Pressure? now, double seconds)
    {
        if (before is null || now is null)
        {
            return null;
        }

        double? Share(ulong? earlier, ulong? later) =>
            earlier is { } from && later is { } to && to >= from ? Percent((to - from) / (seconds * 1e6)) : null;

        return new PressureLoad
        {
            CpuPercent = Share(before.Cpu?.SomeMicros, now.Cpu?.SomeMicros),
            MemoryPercent = Share(before.Memory?.SomeMicros, now.Memory?.SomeMicros),
            IoPercent = Share(before.Io?.SomeMicros, now.Io?.SomeMicros),
            CpuFullPercent = Share(before.Cpu?.FullMicros, now.Cpu?.FullMicros),
            MemoryFullPercent = Share(before.Memory?.FullMicros, now.Memory?.FullMicros),
            IoFullPercent = Share(before.Io?.FullMicros, now.Io?.FullMicros),
        };
    }

    /// <summary>Disks in both readings whose counters only grew; a disk that was swapped out is left out.</summary>
    private static List<DiskLoad> DiskRates(HostStatus before, HostStatus now, double seconds)
    {
        var earlier = before.Disks.GroupBy(disk => disk.DeviceName, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var rates = new List<DiskLoad>();
        foreach (var disk in now.Disks)
        {
            if (!earlier.TryGetValue(disk.DeviceName, out var previous)
                || disk.ReadBytes < previous.ReadBytes || disk.WrittenBytes < previous.WrittenBytes || disk.BusyMillis < previous.BusyMillis)
            {
                continue;
            }

            rates.Add(new DiskLoad
            {
                DeviceName = disk.DeviceName,
                Port = disk.Port,
                ReadBytesPerSecond = (disk.ReadBytes - previous.ReadBytes) / seconds,
                WrittenBytesPerSecond = (disk.WrittenBytes - previous.WrittenBytes) / seconds,
                BusyPercent = Percent((disk.BusyMillis - previous.BusyMillis) / (seconds * 1000)),
            });
        }

        return rates;
    }

    /// <summary>
    /// Each pool's traffic: the sum over the distinct disks its vdevs are on. A disk split
    /// between two pools counts in both, since diskstats cannot say which pool its traffic was for.
    /// </summary>
    private static List<PoolLoad> PoolRates(List<DiskLoad> disks, ZfsStatus? zfs)
    {
        if (zfs is null || disks.Count == 0)
        {
            return [];
        }

        var byName = disks.ToDictionary(disk => disk.DeviceName, StringComparer.Ordinal);
        return
        [
            .. zfs.Pools.Select(pool =>
            {
                var members = pool.Vdevs.Select(vdev => vdev.DeviceName).OfType<string>().Distinct(StringComparer.Ordinal)
                    .Select(name => byName.GetValueOrDefault(name)).OfType<DiskLoad>().ToList();
                return (pool.Name, members);
            })
            .Where(pool => pool.members.Count > 0)
            .Select(pool => new PoolLoad
            {
                Name = pool.Name,
                ReadBytesPerSecond = pool.members.Sum(disk => disk.ReadBytesPerSecond),
                WrittenBytesPerSecond = pool.members.Sum(disk => disk.WrittenBytesPerSecond),
            }),
        ];
    }

    private static List<InterfaceLoad> InterfaceRates(HostStatus before, HostStatus now, double seconds)
    {
        var rates = new List<InterfaceLoad>();
        foreach (var interfaceNow in now.Interfaces)
        {
            if (before.Interfaces.FirstOrDefault(earlier => earlier.Name == interfaceNow.Name) is not { } previous
                || interfaceNow.ReceivedBytes < previous.ReceivedBytes || interfaceNow.TransmittedBytes < previous.TransmittedBytes)
            {
                continue;
            }

            rates.Add(new InterfaceLoad
            {
                Name = interfaceNow.Name,
                ReceivedBytesPerSecond = (interfaceNow.ReceivedBytes - previous.ReceivedBytes) / seconds,
                TransmittedBytesPerSecond = (interfaceNow.TransmittedBytes - previous.TransmittedBytes) / seconds,
            });
        }

        return rates;
    }

    /// <summary>
    /// A fraction as a percent, held to 0-100: the kernel's busy and stall counters are sampled
    /// at slightly different instants from the clock, so a saturated disk can read 100.4 %.
    /// </summary>
    private static double Percent(double fraction) => Math.Clamp(fraction * 100, 0, 100);
}
