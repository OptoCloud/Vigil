using Vigil.Core.Alerting;
using Vigil.Core.Protocol;
using Xunit;
using static Vigil.Core.Tests.PoolSamples;

namespace Vigil.Core.Tests;

public sealed class PoolDiffTests
{
    private const string Start = "2026-10-08T04:46:54.000Z";
    private const string End = "2026-10-08T09:10:54.000Z";

    [Fact]
    public void TheFirstPollRecordsWhereThingsStandButAnnouncesNothing()
    {
        var current = Zfs(Pool(
            state: "DEGRADED",
            scan: Scan("RESILVER", "SCANNING", Start),
            vdevs: [Disk("a", "1", "FAULTED", read: 3), Disk("b", "2")]));

        var update = PoolDiff.Diff(null, current);

        Assert.Empty(update.Events);
        Assert.Single(update.Scans);
        var row = Assert.Single(update.VdevErrors);
        Assert.Equal(("void", "1", 3UL), (row.Pool, row.VdevGuid, row.Read!.Value));
    }

    [Fact]
    public void AnUnreadablePollSaysNothingAtAll()
    {
        Assert.Same(PoolUpdate.Empty, PoolDiff.Diff(Zfs(Pool()), Unreadable()));
    }

    [Fact]
    public void AResilverIsAnnouncedWhenItStartsAndWhenItFinishesWithItsDuration()
    {
        var before = Zfs(Pool());
        var running = Zfs(Pool(state: "DEGRADED", scan: Scan("RESILVER", "SCANNING", Start)));
        var done = Zfs(Pool(scan: Scan("RESILVER", "FINISHED", Start, End)));

        var started = Assert.Single(PoolDiff.Diff(before, running).Events);
        Assert.Equal((Severity.Warning, "zfs-resilver:void"), (started.Severity, started.Kind));

        // Still the same resilver: nothing new.
        Assert.Empty(PoolDiff.Diff(running, running with { AsOf = "later" }).Events);

        var finished = Assert.Single(PoolDiff.Diff(running, done).Events);
        Assert.Equal((Severity.Info, "zfs-resilver:void"), (finished.Severity, finished.Kind));
        Assert.Contains("in 4 h 24 min, with no errors", finished.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AResilverThatFinishedWithErrorsIsAWarning()
    {
        var running = Zfs(Pool(scan: Scan("RESILVER", "SCANNING", Start)));
        var done = Zfs(Pool(scan: Scan("RESILVER", "FINISHED", Start, End, errors: 2)));

        var finished = Assert.Single(PoolDiff.Diff(running, done).Events);
        Assert.Equal(Severity.Warning, finished.Severity);
        Assert.Contains("with 2 errors", finished.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AScrubIsNewsOnlyWhenItFinishesWithErrors()
    {
        var running = Zfs(Pool(scan: Scan("SCRUB", "SCANNING", Start)));

        Assert.Empty(PoolDiff.Diff(Zfs(Pool()), running).Events);
        Assert.Empty(PoolDiff.Diff(running, Zfs(Pool(scan: Scan("SCRUB", "FINISHED", Start, End)))).Events);

        var bad = Assert.Single(PoolDiff.Diff(running, Zfs(Pool(scan: Scan("SCRUB", "FINISHED", Start, End, errors: 5)))).Events);
        Assert.Equal((Severity.Warning, "zfs-scrub:void"), (bad.Severity, bad.Kind));
    }

    [Fact]
    public void ErrorCountersThatGrowAreAWarningAndARowButAClearIsOnlyARow()
    {
        var clean = Zfs(Pool());
        var errors = Zfs(Pool(vdevs: [Disk("a", "1", read: 2, checksum: 1), Disk("b", "2")]));

        var grew = PoolDiff.Diff(clean, errors);
        var warning = Assert.Single(grew.Events);
        Assert.Equal("zfs-vdev-errors:void:1", warning.Kind);
        Assert.Contains("read 0 → 2, checksum 0 → 1", warning.Message, StringComparison.Ordinal);
        Assert.Single(grew.VdevErrors);

        // zpool clear: the counts go back to zero, which is history but not news.
        var cleared = PoolDiff.Diff(errors, clean);
        Assert.Empty(cleared.Events);
        Assert.Single(cleared.VdevErrors);

        Assert.Empty(PoolDiff.Diff(clean, clean).VdevErrors);
    }

    [Fact]
    public void AScanIsRecordedOnlyWhenItChanged()
    {
        var running = Zfs(Pool(scan: Scan("SCRUB", "SCANNING", Start)));

        Assert.Empty(PoolDiff.Diff(running, running).Scans);
        Assert.Single(PoolDiff.Diff(running, Zfs(Pool(scan: Scan("SCRUB", "FINISHED", Start, End)))).Scans);
    }
}
