using Vigil.Core.Alerting;
using Vigil.Core.Protocol;
using Xunit;

namespace Vigil.Core.Tests;

public sealed class DriveConditionsTests
{
    private static DriveState Drive(bool? passed = true, bool? selfTest = true, ulong? hours = 20112, ulong? testedAt = 20000) => new()
    {
        Wwn = "naa.1",
        Port = null,
        Passed = passed,
        ReallocatedSectorCount = 0,
        PendingSectorCount = 0,
        PowerOnHours = hours,
        SerialNumber = "ZDH8XK2P",
        LastSelfTestPassed = selfTest,
        LastSelfTestHours = testedAt,
        SourcePath = "/dev/sda",
        AsOf = "t",
    };

    private static readonly TimeSpan EightDays = TimeSpan.FromDays(8);

    [Fact]
    public void AHealthyRecentlyTestedDriveHasNone()
    {
        Assert.Empty(DriveConditions.Of([Drive()], EightDays));
    }

    [Fact]
    public void AFailedAssessmentAndAFailedSelfTestAreEachCriticalAndNameTheSerial()
    {
        var conditions = DriveConditions.Of([Drive(passed: false, selfTest: false)], null);

        Assert.Equal(["drive-failed:naa.1", "drive-self-test:naa.1"], conditions.Keys);
        Assert.All(conditions.Values, c => Assert.Equal(Severity.Critical, c.Severity));
        Assert.StartsWith("Drive naa.1 (serial ZDH8XK2P) FAILED", conditions["drive-failed:naa.1"].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFailedDriveSaysWhichBayItWasLastSeenIn()
    {
        var bays = BayNames.Of([new Bay { Port = "pci-0000:01:00.1-ata-4", Cage = "4-bay", Slot = 2 }]);

        var failed = DriveConditions.Of([Drive(passed: false) with { Port = "pci-0000:01:00.1-ata-4" }], null, bays);
        var unplaced = DriveConditions.Of([Drive(passed: false) with { SerialNumber = null }], null, bays);

        Assert.StartsWith("Drive naa.1 (serial ZDH8XK2P, 4-bay slot 2) FAILED", failed["drive-failed:naa.1"].Message, StringComparison.Ordinal);
        Assert.StartsWith("Drive naa.1 FAILED", unplaced["drive-failed:naa.1"].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AStaleSelfTestIsAWarningOnlyWhenAgeIsJudged()
    {
        var stale = Drive(hours: 20112, testedAt: 19900);

        Assert.Empty(DriveConditions.Of([stale], null));
        Assert.Equal(Severity.Warning, DriveConditions.Of([stale], EightDays)["drive-self-test-age:naa.1"].Severity);
        Assert.Empty(DriveConditions.Of([Drive(testedAt: null)], EightDays));
    }

    [Theory]
    [InlineData(20112UL, 20000UL, 112UL)]
    [InlineData(70000UL, 4400UL, 64UL)] // 69936 wrapped at 16 bits: 64 hours, not 7.5 years
    [InlineData(70000UL, 69990UL, 10UL)] // a drive that logs the full count
    [InlineData(100UL, 200UL, 65436UL)] // logged hours ahead of the clock: read as wrapped, so stale
    public void SelfTestAgeAllowsForSixteenBitHours(ulong hours, ulong testedAt, ulong age)
    {
        Assert.Equal(age, DriveConditions.SelfTestAgeHours(Drive(hours: hours, testedAt: testedAt)));
    }
}
