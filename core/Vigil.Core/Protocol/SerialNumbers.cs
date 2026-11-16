using System.Text.RegularExpressions;

namespace Vigil.Core.Protocol;

/// <summary>
/// Drive serial numbers as vigil-core holds them: the last few characters only, wherever a
/// snapshot carries one.
/// </summary>
/// <remarks>
/// The dashboard is public (ADR-023), and a full serial is what a manufacturer's warranty form
/// asks for: published next to the model and the drive's health, it lets a stranger check the
/// warranty and open a claim against the disk. Its job on the page is to say which disk to pull,
/// and the tail printed on the label does that, with the bay. Masked where a serial enters the
/// process (vigild's snapshot, the database), as <see cref="Clients.NutProtocol"/> masks
/// credentials, so the live stream, the event log and a notification cannot carry a whole one.
/// The full serial stays where it is needed: on the host, from <c>smartctl</c>.
/// <para>
/// A serial arrives in two places. SMART reports it as a field; and a pool built on
/// <c>/dev/disk/by-id/ata-MODEL_SERIAL</c> links carries it inside every vdev's name and path,
/// which is how rpool and scratch published theirs after the field was masked.
/// </para>
/// </remarks>
public static partial class SerialNumbers
{
    /// <summary>Enough to tell thirteen disks apart on their labels; far too little to file a claim.</summary>
    public const int Shown = 4;

    /// <summary>What the database holds from the releases that elided with an ellipsis; read as already cut.</summary>
    private const char LegacyElided = '…';

    /// <summary>
    /// A by-id link's name, split into what may hold a serial and what follows one: an NVMe
    /// namespace (<c>_1</c>) and a partition (<c>-part3</c>). udev builds the name as
    /// <c>bus-MODEL_SERIAL</c>, so the serial is whatever follows the stem's last underscore;
    /// <c>wwn-0x…</c>, <c>nvme-eui.…</c> and <c>raidz2-0</c> have none and are left alone.
    /// </summary>
    [GeneratedRegex(@"^(?<stem>[A-Za-z0-9]+-.+?)(?<rest>(_\d{1,2})?(-part\d+)?)$", RegexOptions.CultureInvariant)]
    private static partial Regex LinkName();

    /// <summary>The tail of <paramref name="serial"/>. Masking twice changes nothing.</summary>
    public static string? Mask(string? serial)
    {
        if (serial is null)
        {
            return null;
        }

        var trimmed = serial.Trim();
        if (trimmed.StartsWith(Redaction.Marker, StringComparison.Ordinal))
        {
            return trimmed;
        }

        if (trimmed.StartsWith(LegacyElided))
        {
            // Already a tail: cutting it again would leave nothing to tell the disks apart by.
            return $"{Redaction.Marker}{trimmed.TrimStart(LegacyElided)}";
        }

        return trimmed.Length > Shown ? $"{Redaction.Marker}{trimmed[^Shown..]}" : Redaction.Marker;
    }

    /// <summary>
    /// A vdev's name or path with the serial in it masked. <paramref name="knownSerials"/> are
    /// cut out wherever they appear; the shape of a by-id name covers a disk SMART could not
    /// name (asleep, or no smartctl), which is exactly when nothing else would.
    /// </summary>
    public static string MaskLink(string link, IReadOnlyCollection<string> knownSerials)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(knownSerials);

        foreach (var serial in knownSerials)
        {
            // udev writes a serial's spaces as underscores.
            link = link.Replace(serial, Mask(serial), StringComparison.Ordinal)
                .Replace(serial.Replace(' ', '_'), Mask(serial), StringComparison.Ordinal);
        }

        // The name alone: a start offset would not move what the pattern's ^ means.
        var nameStart = link.LastIndexOf('/') + 1;
        var match = LinkName().Match(link[nameStart..]);
        if (!match.Success)
        {
            return link;
        }

        var stem = match.Groups["stem"].Value;
        var serialStart = stem.LastIndexOf('_') + 1;
        return serialStart == 0 ? link : $"{link[..nameStart]}{stem[..serialStart]}{Mask(stem[serialStart..])}{match.Groups["rest"].Value}";
    }

    /// <summary><paramref name="snapshot"/> with every serial in it masked: each drive's, and the one in a vdev's by-id name.</summary>
    public static Snapshot Mask(Snapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        // Anything shorter than what is shown would match by accident, and is not a secret.
        string[] known = [.. snapshot.DriveHealth
            .Select(drive => drive.SerialNumber?.Trim())
            .OfType<string>()
            .Where(serial => serial.Length > Shown && !serial.StartsWith(Redaction.Marker, StringComparison.Ordinal))];

        return snapshot with
        {
            DriveHealth = [.. snapshot.DriveHealth.Select(drive => drive with { SerialNumber = Mask(drive.SerialNumber) })],
            Zfs = snapshot.Zfs is { } zfs
                ? zfs with { Pools = [.. zfs.Pools.Select(pool => pool with { Vdevs = [.. pool.Vdevs.Select(vdev => Mask(vdev, known))] })] }
                : null,
        };
    }

    private static VdevStatus Mask(VdevStatus vdev, string[] known) =>
        vdev with { Name = MaskLink(vdev.Name, known), Path = vdev.Path is { } path ? MaskLink(path, known) : null };
}
