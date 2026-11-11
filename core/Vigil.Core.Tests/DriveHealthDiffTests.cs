using Vigil.Core.Alerting;
using Vigil.Core.Protocol;
using Xunit;

namespace Vigil.Core.Tests;

/// <summary>Ported from the Rust core's alerts.rs tests: the tests are the specification.</summary>
public sealed class DriveHealthDiffTests
{
    private static DriveHealth Health(bool? passed, ulong? reallocated, ulong? pending, bool available) => new()
    {
        DeviceName = "naa.1",
        Port = "pci-0000:01:00.1-ata-3",
        Passed = passed,
        ReallocatedSectorCount = reallocated,
        PendingSectorCount = pending,
        PowerOnHours = 100,
        SourcePath = "/dev/sdc",
        IsAvailable = available,
        AsOf = "t",
    };

    private static DriveState FirstSighting(DriveHealth health) =>
        DriveHealthDiff.Diff(null, health).State ?? throw new InvalidOperationException("an available drive always has a state");

    [Fact]
    public void ASleepingDriveLeavesItsLastGoodStateAlone()
    {
        var update = DriveHealthDiff.Diff(null, Health(null, null, null, false));

        Assert.Null(update.State);
        Assert.False(update.Changed);
        Assert.Empty(update.Events);
    }

    [Fact]
    public void TheFirstSightingIsRecordedWithoutAlertingOnAStableCount()
    {
        var update = DriveHealthDiff.Diff(null, Health(true, 1048, 0, true));

        Assert.True(update.Changed);
        Assert.Empty(update.Events);
        Assert.Equal("pci-0000:01:00.1-ata-3", update.State?.Port);
    }

    [Fact]
    public void AlertsOnGrowthAndPendingSectorsAndOnRecoveryButLeavesFailureToTheConditions()
    {
        var previous = FirstSighting(Health(true, 8, 0, true));

        var worse = DriveHealthDiff.Diff(previous, Health(false, 16, 2, true));
        Assert.Equal(
            [
                ("drive-reallocated:naa.1", Severity.Warning),
                ("drive-pending:naa.1", Severity.Warning),
            ],
            worse.Events.Select(e => (e.Kind, e.Severity)));
        Assert.Contains("grew from 8 to 16", worse.Events[0].Message, StringComparison.Ordinal);
        Assert.True(worse.Changed);

        var better = DriveHealthDiff.Diff(worse.State, Health(true, 16, 0, true));
        Assert.Equal(Severity.Info, Assert.Single(better.Events).Severity);
    }

    [Fact]
    public void GrowthInTheEarlyWarningCountersIsAWarningEach()
    {
        var counters = Health(true, 0, 0, true) with { ReportedUncorrect = 0, CommandTimeout = 4295032833, UdmaCrcErrorCount = 3 };
        var previous = FirstSighting(counters);

        var update = DriveHealthDiff.Diff(previous, counters with { ReportedUncorrect = 2, CommandTimeout = 4295032834, UdmaCrcErrorCount = 9 });

        Assert.Equal(
            ["drive-uncorrect:naa.1", "drive-timeout:naa.1", "drive-crc:naa.1"],
            update.Events.Select(e => e.Kind));
        Assert.All(update.Events, e => Assert.Equal(Severity.Warning, e.Severity));
        Assert.Contains("cable", update.Events[2].Message, StringComparison.Ordinal);
        Assert.True(update.Changed);

        // A first sighting with high counts is history, not news.
        Assert.Empty(DriveHealthDiff.Diff(null, counters with { UdmaCrcErrorCount = 500 }).Events);
    }

    [Fact]
    public void ASelfTestResultChangingIsAHistoryRow()
    {
        var previous = FirstSighting(Health(true, 0, 0, true) with { LastSelfTestPassed = true });

        Assert.True(DriveHealthDiff.Diff(previous, Health(true, 0, 0, true) with { LastSelfTestPassed = false }).Changed);
    }

    [Fact]
    public void OnlyPowerOnHoursMovingIsNotAChange()
    {
        var previous = FirstSighting(Health(true, 8, 0, true));
        var next = Health(true, 8, 0, true) with { PowerOnHours = 101 };

        Assert.False(DriveHealthDiff.Diff(previous, next).Changed);
    }

    [Fact]
    public void AFailedDriveIsAnnouncedOnceNotOnEveryPoll()
    {
        var failed = FirstSighting(Health(false, 8, 0, true));

        Assert.Empty(DriveHealthDiff.Diff(failed, Health(false, 8, 0, true)).Events);
    }
}
