using Vigil.Core.Protocol;

namespace Vigil.Core.Alerting;

/// <summary>What one drive's health poll means against its last good state.</summary>
/// <param name="State">The drive's new last-good state, or null if this poll had nothing usable (asleep, smartctl failed).</param>
/// <param name="Changed">True if anything other than power-on hours moved, i.e. worth a history row.</param>
public sealed record DriveUpdate(DriveState? State, bool Changed, IReadOnlyList<NewEvent> Events);

public static class DriveHealthDiff
{
    /// <summary>
    /// Compares one drive's fresh health poll with its last good state.
    /// </summary>
    /// <remarks>
    /// <c>smart_status.passed</c> is a lagging indicator (drives routinely die with it still
    /// true), so sector counts are watched too, but on CHANGE rather than level: a drive with a
    /// long-standing, stable reallocated count would otherwise alert forever.
    /// </remarks>
    public static DriveUpdate Diff(DriveState? previous, DriveHealth current)
    {
        if (!current.IsAvailable)
        {
            return new DriveUpdate(null, false, []);
        }

        var state = new DriveState
        {
            Wwn = current.DeviceName,
            Port = current.Port,
            Passed = current.Passed,
            ReallocatedSectorCount = current.ReallocatedSectorCount,
            PendingSectorCount = current.PendingSectorCount,
            PowerOnHours = current.PowerOnHours,
            SerialNumber = current.SerialNumber,
            ReportedUncorrect = current.ReportedUncorrect,
            CommandTimeout = current.CommandTimeout,
            UdmaCrcErrorCount = current.UdmaCrcErrorCount,
            LastSelfTestPassed = current.LastSelfTestPassed,
            LastSelfTestHours = current.LastSelfTestHours,
            SourcePath = current.SourcePath,
            AsOf = current.AsOf,
        };

        var events = new List<NewEvent>();
        var name = $"Drive {current.DeviceName} ({current.SourcePath})";
        NewEvent Event(Severity severity, string kind, string message) => new(severity, $"{kind}:{state.Wwn}", message);

        // A failed assessment or self-test is not announced here: they are conditions
        // (DriveConditions), so they hold the verdict for as long as they last.

        // Growth only, and only against a known previous count: a first sighting of a drive
        // with old reallocations is history, not news.
        void Grew(ulong? now, ulong? before, string kind, string what)
        {
            if (now is { } count && before is { } previousCount && count > previousCount)
            {
                events.Add(Event(Severity.Warning, kind, $"{name}: {what} grew from {previousCount} to {count}."));
            }
        }

        Grew(current.ReallocatedSectorCount, previous?.ReallocatedSectorCount, "drive-reallocated", "reallocated sector count");
        Grew(current.ReportedUncorrect, previous?.ReportedUncorrect, "drive-uncorrect", "reported uncorrectable errors (187)");
        Grew(current.CommandTimeout, previous?.CommandTimeout, "drive-timeout", "command timeouts (188)");
        Grew(
            current.UdmaCrcErrorCount,
            previous?.UdmaCrcErrorCount,
            "drive-crc",
            "UDMA CRC errors (199), which usually means the cable or the backplane,");

        // Pending sectors are different: any nonzero count is news, because they either get
        // reallocated or read back, and both are worth knowing.
        var previousPending = previous?.PendingSectorCount ?? 0;
        if (current.PendingSectorCount is { } pending && pending != previousPending)
        {
            events.Add(pending > 0
                ? Event(
                    Severity.Warning,
                    "drive-pending",
                    $"{name}: {pending} pending (unreadable, not yet reallocated) sector(s), was {previousPending}.")
                : Event(Severity.Info, "drive-pending", $"{name}: no pending sectors any more (was {previousPending})."));
        }

        var changed = previous is null
            || previous.Passed != state.Passed
            || previous.ReallocatedSectorCount != state.ReallocatedSectorCount
            || previous.PendingSectorCount != state.PendingSectorCount
            || previous.ReportedUncorrect != state.ReportedUncorrect
            || previous.CommandTimeout != state.CommandTimeout
            || previous.UdmaCrcErrorCount != state.UdmaCrcErrorCount
            || previous.LastSelfTestPassed != state.LastSelfTestPassed;

        return new DriveUpdate(state, changed, events);
    }
}
