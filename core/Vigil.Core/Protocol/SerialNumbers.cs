namespace Vigil.Core.Protocol;

/// <summary>
/// Drive serial numbers as vigil-core holds them: the last few characters only.
/// </summary>
/// <remarks>
/// The dashboard is public (ADR-023), and a full serial is what a manufacturer's warranty form
/// asks for: published next to the model and the drive's health, it lets a stranger check the
/// warranty and open a claim against the disk. Its job on the page is to say which disk to pull,
/// and the tail printed on the label does that, with the bay. Masked where a serial enters the
/// process (vigild's snapshot, the database), as <see cref="Clients.NutProtocol"/> masks
/// credentials, so the live stream, the event log and a notification cannot carry a whole one.
/// The full serial stays where it is needed: on the host, from <c>smartctl</c>.
/// </remarks>
public static class SerialNumbers
{
    /// <summary>Enough to tell thirteen disks apart on their labels; far too little to file a claim.</summary>
    public const int Shown = 4;

    /// <summary>What stands for the part left out, and marks a serial as already masked.</summary>
    public const char Elided = '…';

    /// <summary>The tail of <paramref name="serial"/>. Masking twice changes nothing.</summary>
    public static string? Mask(string? serial)
    {
        if (serial is null)
        {
            return null;
        }

        var trimmed = serial.Trim();
        if (trimmed.StartsWith(Elided))
        {
            return trimmed;
        }

        return trimmed.Length > Shown ? $"{Elided}{trimmed[^Shown..]}" : Elided.ToString();
    }

    /// <summary><paramref name="snapshot"/> with every drive's serial masked.</summary>
    public static Snapshot Mask(Snapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot with { DriveHealth = [.. snapshot.DriveHealth.Select(drive => drive with { SerialNumber = Mask(drive.SerialNumber) })] };
    }
}
