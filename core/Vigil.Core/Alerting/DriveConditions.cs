using Vigil.Core.Protocol;

namespace Vigil.Core.Alerting;

/// <summary>
/// What about a drive holds the verdict (ADR-019) rather than being a one-off event: a failed
/// overall assessment, a failed self-test, and, once smartd schedules tests, a self-test gone
/// stale. Judged on each drive's last GOOD state, so a failed drive that has since gone to
/// sleep stays failed rather than vanishing from the verdict.
/// </summary>
public static class DriveConditions
{
    /// <summary>
    /// Some drives keep the self-test log's lifetime hours in 16 bits, so past 65535 power-on
    /// hours the logged value has wrapped and plain subtraction says the test is years old.
    /// </summary>
    private const ulong SelfTestHoursModulus = 1UL << 16;

    /// <param name="selfTestMaxAge">
    /// How old the newest completed self-test may be, <c>SMART_SELF_TEST_MAX_AGE_DAYS</c>. Null
    /// leaves age unjudged: without smartd scheduling tests every drive would be overdue.
    /// </param>
    /// <param name="bays">Each port's bay name (<see cref="BayNames"/>); a drive's last known port finds it.</param>
    public static Conditions Of(IEnumerable<DriveState> drives, TimeSpan? selfTestMaxAge, IReadOnlyDictionary<string, string>? bays = null)
    {
        bays ??= BayNames.Of([]);
        ArgumentNullException.ThrowIfNull(drives);
        var conditions = new Conditions();
        foreach (var drive in drives)
        {
            var serial = drive.SerialNumber is { } number ? $", serial {number}" : string.Empty;
            var bay = bays.TryGetValue(drive.Port ?? string.Empty, out var place) ? $", {place}" : string.Empty;
            var name = $"Drive {drive.Wwn}{(serial.Length + bay.Length > 0 ? $" ({(serial + bay)[2..]})" : string.Empty)}";

            if (drive.Passed == false)
            {
                conditions[$"drive-failed:{drive.Wwn}"] = new(
                    Severity.Critical,
                    $"{name} FAILED its SMART overall-health self-assessment.",
                    $"{name} passes its SMART self-assessment again.");
            }

            if (drive.LastSelfTestPassed == false)
            {
                conditions[$"drive-self-test:{drive.Wwn}"] = new(
                    Severity.Critical,
                    $"{name} failed its last SMART self-test.",
                    $"{name} passed a SMART self-test again.");
            }

            if (selfTestMaxAge is { } maxAge && SelfTestAgeHours(drive) is { } age && age > maxAge.TotalHours)
            {
                conditions[$"drive-self-test-age:{drive.Wwn}"] = new(
                    Severity.Warning,
                    $"{name} last completed a SMART self-test {age / 24} days ago (allowed {maxAge.TotalDays:0}). Is smartd scheduling them?",
                    $"{name} has run a SMART self-test again.");
            }
        }

        return conditions;
    }

    /// <summary>
    /// Power-on hours since the newest completed self-test. When the logged hours could have
    /// wrapped, the shorter reading is taken: a recent test misread as ancient would alert,
    /// while the opposite misreading needs a drive that ran no test for 7.5 years.
    /// </summary>
    internal static ulong? SelfTestAgeHours(DriveState drive)
    {
        if (drive.PowerOnHours is not { } now || drive.LastSelfTestHours is not { } then)
        {
            return null;
        }

        if (then >= SelfTestHoursModulus)
        {
            return now >= then ? now - then : 0;
        }

        return ((now % SelfTestHoursModulus) + SelfTestHoursModulus - then) % SelfTestHoursModulus;
    }
}
