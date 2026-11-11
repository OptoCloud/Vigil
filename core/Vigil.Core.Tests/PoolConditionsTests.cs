using Vigil.Core.Alerting;
using Vigil.Core.Configuration;
using Vigil.Core.Protocol;
using Xunit;
using static Vigil.Core.Tests.PoolSamples;

namespace Vigil.Core.Tests;

public sealed class PoolConditionsTests
{
    private static PoolJudgement Judge(
        DateTimeOffset? now = null,
        Dictionary<string, DateTimeOffset>? scrubs = null,
        Dictionary<string, int>? spares = null,
        Func<string, bool>? isRaised = null) => new(
            now ?? At,
            scrubs ?? [],
            TimeSpan.FromDays(35),
            spares ?? [],
            isRaised ?? (_ => false));

    [Fact]
    public void AHealthyPoolHasNoConditions()
    {
        Assert.Empty(PoolConditions.Of(Zfs(Pool()), Judge(spares: new() { ["void"] = 2 })));
    }

    [Fact]
    public void NothingIsJudgedWhenVigildDoesNotReportZfs()
    {
        Assert.Empty(PoolConditions.Of(null, Judge()));
        Assert.True(PoolConditions.IsComplete(null));
    }

    [Fact]
    public void ADegradedPoolItsFaultedDiskAndTheSpareInUseAreEachCritical()
    {
        var zfs = Zfs(Pool(
            state: "DEGRADED",
            vdevs: [Disk("a", "1", "FAULTED"), Disk("b", "2"), Spare("s1", "8", "INUSE"), Spare("s2", "9")]));

        var conditions = PoolConditions.Of(zfs, Judge());

        Assert.Equal(["zfs-pool:void", "zfs-vdev:void:1", "zfs-vdev:void:8"], conditions.Keys);
        Assert.All(conditions.Values, condition => Assert.Equal(Severity.Critical, condition.Severity));
        Assert.Contains("FAULTED (ERR_EXCEEDED)", conditions["zfs-vdev:void:1"].Message, StringComparison.Ordinal);
        Assert.Contains("standing in for a failed disk", conditions["zfs-vdev:void:8"].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PermanentDataErrorsAreCriticalEvenOnAnOnlinePool()
    {
        var conditions = PoolConditions.Of(Zfs(Pool(errorCount: 4)), Judge());

        Assert.Equal(Severity.Critical, conditions["zfs-data-errors:void"].Severity);
    }

    [Fact]
    public void AnUnreadablePollRaisesOnlyThatAndIsIncomplete()
    {
        var zfs = Unreadable();

        Assert.Equal([PoolConditions.Unreadable], PoolConditions.Of(zfs, Judge()).Keys);
        Assert.False(PoolConditions.IsComplete(zfs));
    }

    [Fact]
    public void APollOlderThanFiveMinutesIsUnreadable()
    {
        Assert.Empty(PoolConditions.Of(Zfs(Pool()), Judge(now: At.AddMinutes(5))));
        Assert.Contains(PoolConditions.Unreadable, PoolConditions.Of(Zfs(Pool()), Judge(now: At.AddMinutes(6))).Keys);
    }

    [Fact]
    public void CapacityWarnsAtEightyIsCriticalAtNinetyAndClearsOnlyTwoPointsBelow()
    {
        Assert.Empty(PoolConditions.Of(Zfs(Pool(allocated: 799)), Judge()));
        Assert.Equal(["zfs-capacity:void"], PoolConditions.Of(Zfs(Pool(allocated: 800)), Judge()).Keys);

        var full = PoolConditions.Of(Zfs(Pool(allocated: 900)), Judge());
        Assert.Equal(Severity.Critical, full["zfs-capacity-critical:void"].Severity);
        Assert.Equal(Severity.Warning, full["zfs-capacity:void"].Severity);

        // Raised, it holds down to 78 %, so a pool hovering at the line does not flap.
        Assert.Contains("zfs-capacity:void", PoolConditions.Of(Zfs(Pool(allocated: 785)), Judge(isRaised: _ => true)).Keys);
        Assert.Empty(PoolConditions.Of(Zfs(Pool(allocated: 779)), Judge(isRaised: _ => true)));
    }

    [Fact]
    public void AnOverdueScrubIsAWarningWhicheverSourceRemembersIt()
    {
        var recent = Zfs(Pool(scan: Scan("SCRUB", "FINISHED", "2026-09-10T00:00:00.000Z", "2026-09-10T06:00:00.000Z")));

        Assert.Empty(PoolConditions.Of(recent, Judge()));
        Assert.Contains("zfs-scrub-age:void", PoolConditions.Of(recent, Judge(now: At.AddDays(10))).Keys);

        // A resilver has replaced the scrub in zpool's report; the database still knows it.
        var resilvered = Zfs(Pool(scan: Scan("RESILVER", "FINISHED", "2026-10-08T04:46:54.000Z", "2026-10-08T09:10:54.000Z")));
        var remembered = new Dictionary<string, DateTimeOffset> { ["void"] = At.AddDays(-40) };
        Assert.Contains("zfs-scrub-age:void", PoolConditions.Of(resilvered, Judge(scrubs: remembered)).Keys);
    }

    [Fact]
    public void APoolWithNoKnownScrubIsNotJudged()
    {
        // Without a database, "never scrubbed" and "not remembered" look the same.
        var later = At.AddYears(1);
        Assert.Empty(PoolConditions.Of(Zfs(Pool(), asOf: Rfc3339.From(later)), Judge(now: later)));
    }

    [Fact]
    public void FewerAvailableSparesThanExpectedIsAWarning()
    {
        var zfs = Zfs(Pool(vdevs: [Disk("a", "1"), Spare("s1", "8", "INUSE"), Spare("s2", "9")]));

        var conditions = PoolConditions.Of(zfs, Judge(spares: new() { ["void"] = 2 }));

        Assert.Equal(Severity.Warning, conditions["zfs-spares:void"].Severity);
        Assert.Contains("1 of its 2 hot spares", conditions["zfs-spares:void"].Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("void:2", 1)]
    [InlineData(" void:2 , tank:1 ", 2)]
    public void SpareCountsParse(string text, int pools)
    {
        Assert.True(SpareCounts.TryParse(text, out var counts, out _));
        Assert.Equal(pools, counts.Count);
    }

    [Theory]
    [InlineData("void")]
    [InlineData("void:0")]
    [InlineData("void:-1")]
    [InlineData(":2")]
    [InlineData("void:2,void:1")]
    public void SpareCountsRefuseAnythingElse(string text)
    {
        Assert.False(SpareCounts.TryParse(text, out _, out var problem));
        Assert.NotEmpty(problem);
    }
}
