using Vigil.Core.Alerting;
using Vigil.Core.Configuration;
using Vigil.Core.Protocol;

namespace Vigil.Core.Runtime;

/// <summary>One SMART poll's meaning: the drives whose state to store, and what to announce.</summary>
internal sealed record DriveObservation(IReadOnlyList<(DriveState State, bool Changed)> ToSave, IReadOnlyList<NewEvent> Events);

/// <summary>
/// The runtime's drive state: each drive's last good SMART result, and the debouncer for the
/// drive conditions. Its own type for the same reason as <see cref="PoolWatch"/>: the runtime
/// is at the size ceiling (docs/STYLE.md §2.1). One owner, no locks.
/// </summary>
/// <remarks>
/// vigild repeats one SMART poll in every snapshot until its next, slower one, so a poll is
/// diffed only when its <c>asOf</c> moves.
/// </remarks>
internal sealed class DriveTracking(VigilOptions options, TimeProvider time)
{
    /// <summary>One poll: SMART's own verdict is a fact about the drive, not a flaky read.</summary>
    private const uint Threshold = 1;

    private readonly ConditionDebouncer _debouncer = new(Threshold, time);
    private readonly SortedDictionary<string, DriveState> _drives = new(StringComparer.Ordinal);
    private readonly TimeSpan? _selfTestMaxAge = options.SmartSelfTestMaxAgeDays is { } days ? TimeSpan.FromDays(days) : null;
    private IReadOnlyDictionary<string, string> _bays = BayNames.Of([]);
    private string _lastAsOf = string.Empty;

    public IEnumerable<Alert> Raised => _debouncer.Raised;

    public List<DriveState> List => [.. _drives.Values];

    /// <summary>Adds what the database remembers, under anything already seen live.</summary>
    public void Remember(IEnumerable<DriveState> drives)
    {
        foreach (var drive in drives)
        {
            _drives.TryAdd(drive.Wwn, drive);
        }
    }

    /// <summary>Takes one snapshot's SMART results; null when this poll was already seen.</summary>
    /// <param name="bays">vigild's <c>[[bays]]</c>, so an alert can say where a failed drive is.</param>
    public DriveObservation? Observe(IReadOnlyList<DriveHealth> health, IReadOnlyList<Bay> bays)
    {
        _bays = BayNames.Of(bays);
        var asOf = health.Count > 0 ? health[0].AsOf : string.Empty;
        if (asOf.Length == 0 || asOf == _lastAsOf)
        {
            return null;
        }

        _lastAsOf = asOf;
        var toSave = new List<(DriveState, bool)>();
        var events = new List<NewEvent>();
        foreach (var drive in health)
        {
            var update = DriveHealthDiff.Diff(_drives.GetValueOrDefault(drive.DeviceName), drive);
            if (update.State is { } state)
            {
                _drives[state.Wwn] = state;
                toSave.Add((state, update.Changed));
                events.AddRange(update.Events);
            }
        }

        return new DriveObservation(toSave, events);
    }

    /// <summary>Judges the drive conditions now; returns what was raised or cleared.</summary>
    public List<NewEvent> Evaluate() => _debouncer.Update(DriveConditions.Of(_drives.Values, _selfTestMaxAge, _bays));
}
