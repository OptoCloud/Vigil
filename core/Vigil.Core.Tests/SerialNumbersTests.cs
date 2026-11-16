using Vigil.Core.Protocol;
using Xunit;

namespace Vigil.Core.Tests;

public sealed class SerialNumbersTests
{
    [Theory]
    [InlineData("ZL2ABCDE", "[REDACTED]BCDE")]
    [InlineData("  WD-WCC4N1234567 ", "[REDACTED]4567")]
    // Too short to show a tail without showing all of it.
    [InlineData("AB12", "[REDACTED]")]
    [InlineData("", "[REDACTED]")]
    public void KeepsOnlyTheTail(string serial, string expected)
    {
        Assert.Equal(expected, SerialNumbers.Mask(serial));
    }

    [Fact]
    public void MaskingTwiceChangesNothingAndNoSerialStaysNone()
    {
        // The database comes to hold masked serials, and they are masked again on the way out.
        Assert.Equal("[REDACTED]BCDE", SerialNumbers.Mask(SerialNumbers.Mask("ZL2ABCDE")));
        Assert.Equal("[REDACTED]", SerialNumbers.Mask("[REDACTED]"));
        Assert.Null(SerialNumbers.Mask((string?)null));
    }

    [Theory]
    [InlineData("…BCDE", "[REDACTED]BCDE")]
    [InlineData("…", "[REDACTED]")]
    public void ASerialTheDatabaseHoldsFromTheEllipsisReleasesReadsAsRedacted(string stored, string expected)
    {
        Assert.Equal(expected, SerialNumbers.Mask(stored));
    }

    [Theory]
    // rpool's and scratch's shape: the serial is the end of the by-id name.
    [InlineData("ata-Samsung_SSD_870_EVO_500GB_S6PXNX0T123456T-part3", "ata-Samsung_SSD_870_EVO_500GB_[REDACTED]456T-part3")]
    [InlineData("/dev/disk/by-id/ata-ST4000DM004-2CV104_ZFN0ABCD-part1", "/dev/disk/by-id/ata-ST4000DM004-2CV104_[REDACTED]ABCD-part1")]
    [InlineData("ata-ST4000DM004-2CV104_ZFN0ABCD", "ata-ST4000DM004-2CV104_[REDACTED]ABCD")]
    [InlineData("scsi-SATA_ST4000VN008-2DR1_ZGY9ZQY6", "scsi-SATA_ST4000VN008-2DR1_[REDACTED]ZQY6")]
    // An NVMe link may end in its namespace, which is not the serial.
    [InlineData("nvme-Samsung_SSD_980_PRO_1TB_S5GXNF0R123456_1-part2", "nvme-Samsung_SSD_980_PRO_1TB_[REDACTED]3456_1-part2")]
    // No serial in these: a WWN is the page's own name for a disk, and the rest are not disks.
    [InlineData("wwn-0x5000c500ccd11e3e-part1", "wwn-0x5000c500ccd11e3e-part1")]
    [InlineData("/dev/disk/by-id/nvme-eui.0025385b11b2c3d4-part1", "/dev/disk/by-id/nvme-eui.0025385b11b2c3d4-part1")]
    [InlineData("raidz2-0", "raidz2-0")]
    [InlineData("mirror-0", "mirror-0")]
    [InlineData("sda1", "sda1")]
    public void AByIdNameLosesTheSerialInItEvenWhenSmartNeverNamedTheDisk(string link, string expected)
    {
        Assert.Equal(expected, SerialNumbers.MaskLink(link, []));
        Assert.Equal(expected, SerialNumbers.MaskLink(expected, []));
    }

    [Fact]
    public void AKnownSerialIsCutOutOfALinkWhateverItsShape()
    {
        // Not the shape of an ata link, so only knowing the serial finds it.
        Assert.Equal("usb-WD_Elements_25A2_[REDACTED]X999-0:0", SerialNumbers.MaskLink("usb-WD_Elements_25A2_WX11AB2CX999-0:0", ["WX11AB2CX999"]));

        // udev writes the space in a serial as an underscore, which would otherwise split it.
        Assert.Equal("ata-HGST_HUH_[REDACTED]K7GH", SerialNumbers.MaskLink("ata-HGST_HUH_2EK_7K7GH", ["2EK 7K7GH"]));
    }

    [Fact]
    public void ASnapshotsVdevsLoseTheSerialsTheirNamesAndPathsCarry()
    {
        var onSerialLink = PoolSamples.Disk("ata-ST4000DM004-2CV104_ZFN0ABCD", "1") with { Path = "/dev/disk/by-id/ata-ST4000DM004-2CV104_ZFN0ABCD-part1" };
        var snapshot = new Snapshot
        {
            TimestampUtc = PoolSamples.AsOf,
            Sensors = [],
            Fans = [],
            DriveHealth = [],
            Zfs = PoolSamples.Zfs(PoolSamples.Pool(vdevs: [onSerialLink, PoolSamples.Disk("0x5000c500ccd11e3e", "2")])),
            ControlLoopHealthy = true,
        };

        var vdevs = Assert.Single(SerialNumbers.Mask(snapshot).Zfs!.Pools).Vdevs;

        Assert.Equal("ata-ST4000DM004-2CV104_[REDACTED]ABCD", vdevs[0].Name);
        Assert.Equal("/dev/disk/by-id/ata-ST4000DM004-2CV104_[REDACTED]ABCD-part1", vdevs[0].Path);
        Assert.Equal("0x5000c500ccd11e3e", vdevs[1].Name);
        Assert.Equal("/dev/disk/by-id/wwn-0x5000c500ccd11e3e-part1", vdevs[1].Path);
    }
}
