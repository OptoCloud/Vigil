using Vigil.Core.Alerting;
using Vigil.Core.Protocol;
using Vigil.Core.Runtime;
using Xunit;

namespace Vigil.Core.Tests;

public sealed class HostConditionsTests
{
    private const ulong Gigabyte = 1024UL * 1024 * 1024;

    private static Conditions Judge(double? memory = null, double? swap = null, double? stall = null, Dictionary<string, Severity>? raised = null) =>
        HostConditions.Of(new HostSustained(memory, swap, stall), kind => raised?.TryGetValue(kind, out var held) == true ? held : null);

    private static Dictionary<string, Severity> Raised(string kind, Severity at) => new(StringComparer.Ordinal) { [kind] = at };

    [Fact]
    public void AnIdleHostAndOneThatReportsNothingRaiseNothing()
    {
        Assert.Empty(Judge(memory: 35, swap: 0, stall: 0.5));
        Assert.Empty(Judge());
    }

    [Theory]
    [InlineData(89.9, null)]
    [InlineData(90, Severity.Warning)]
    [InlineData(95, Severity.Critical)]
    public void MemoryWorsensInOneCondition(double percent, Severity? expected)
    {
        Assert.Equal(expected, Judge(memory: percent).GetValueOrDefault(HostConditions.Memory)?.Severity);
    }

    [Fact]
    public void EachRaisedLevelHoldsUntilThreePointsUnderIt()
    {
        Assert.Equal(Severity.Warning, Judge(memory: 88, raised: Raised(HostConditions.Memory, Severity.Warning))[HostConditions.Memory].Severity);
        Assert.Empty(Judge(memory: 86.9, raised: Raised(HostConditions.Memory, Severity.Warning)));
        Assert.Equal(Severity.Critical, Judge(memory: 93, raised: Raised(HostConditions.Memory, Severity.Critical))[HostConditions.Memory].Severity);
        Assert.Equal(Severity.Warning, Judge(memory: 91.9, raised: Raised(HostConditions.Memory, Severity.Critical))[HostConditions.Memory].Severity);
    }

    [Fact]
    public void SwapHalfUsedAndSustainedMemoryStallAreWarnings()
    {
        var conditions = Judge(memory: 50, swap: 50, stall: 10);

        Assert.Equal([HostConditions.MemoryPressure, HostConditions.Swap], conditions.Keys);
        Assert.All(conditions.Values, condition => Assert.Equal(Severity.Warning, condition.Severity));
        Assert.Empty(Judge(swap: 49.9, stall: 9.9));
    }

    private static HostLoad Load(ulong usedGigabytes, ulong arcGigabytes = 0, double? stall = 0) => new()
    {
        AsOf = "t",
        IntervalSeconds = 2,
        UptimeSeconds = 1,
        Memory = new MemoryLoad
        {
            TotalBytes = 100 * Gigabyte,
            UsedBytes = usedGigabytes * Gigabyte,
            CachedBytes = 0,
            ArcBytes = arcGigabytes * Gigabyte,
            ArcMaxBytes = null,
            SwapTotalBytes = 0,
            SwapUsedBytes = 0,
        },
        Cpu = null,
        Load = null,
        Pressure = stall is { } percent
            ? new PressureLoad { CpuPercent = 0, MemoryPercent = 0, IoPercent = 0, CpuFullPercent = 0, MemoryFullPercent = percent, IoFullPercent = 0 }
            : null,
        Disks = [],
        Pools = [],
        Interfaces = [],
    };

    /// <summary>Feeds a reading every two seconds for <paramref name="span"/>; returns every event.</summary>
    private static List<NewEvent> Feed(HostWatch watch, ManualTime time, TimeSpan span, Func<HostLoad> load)
    {
        var events = new List<NewEvent>();
        for (var elapsed = TimeSpan.Zero; elapsed < span; elapsed += TimeSpan.FromSeconds(2))
        {
            time.Advance(TimeSpan.FromSeconds(2));
            events.AddRange(watch.Observe(load()));
        }

        return events;
    }

    [Fact]
    public void MemoryMustStayFullForTheWholeWindowBeforeItIsRaised()
    {
        var time = new ManualTime();
        var watch = new HostWatch(time);

        // Four minutes at 97 %: not yet sustained, so not yet judged.
        Assert.Empty(Feed(watch, time, TimeSpan.FromMinutes(4), () => Load(97)));
        var raised = Feed(watch, time, TimeSpan.FromMinutes(2), () => Load(97));

        var critical = Assert.Single(raised);
        Assert.Equal((HostConditions.Memory, Severity.Critical), (critical.Kind, critical.Severity));
        Assert.Equal(HostConditions.Memory, Assert.Single(watch.Raised).Kind);
    }

    [Fact]
    public void ABurstInsideAQuietWindowRaisesNothing()
    {
        var time = new ManualTime();
        var watch = new HostWatch(time);

        Feed(watch, time, TimeSpan.FromMinutes(6), () => Load(30));
        var events = Feed(watch, time, TimeSpan.FromSeconds(40), () => Load(99, stall: 60));

        Assert.Empty(events);
    }

    [Fact]
    public void TheArcIsNotCountedAsMemoryInUse()
    {
        var time = new ManualTime();
        var watch = new HostWatch(time);

        // 97 % "used", of which 60 points are the ARC, which ZFS gives back.
        Assert.Empty(Feed(watch, time, TimeSpan.FromMinutes(10), () => Load(97, arcGigabytes: 60)));
    }

    [Fact]
    public void AGapInTheReadingsNeitherRaisesNorClears()
    {
        var time = new ManualTime();
        var watch = new HostWatch(time);
        Feed(watch, time, TimeSpan.FromMinutes(6), () => Load(97));
        Assert.Single(watch.Raised);

        // vigild away for an hour, then back with a healthy host: the old window is gone, and
        // the new one is too short to say anything yet, so the alert stands.
        time.Advance(TimeSpan.FromHours(1));
        Assert.Empty(Feed(watch, time, TimeSpan.FromMinutes(2), () => Load(30)));
        Assert.Single(watch.Raised);

        // Once the healthy readings span the window, it clears.
        var cleared = Feed(watch, time, TimeSpan.FromMinutes(5), () => Load(30));
        Assert.Equal(Severity.Info, Assert.Single(cleared).Severity);
        Assert.Empty(watch.Raised);
    }
}
