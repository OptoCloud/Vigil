using Vigil.Core.Protocol;

namespace Vigil.Core.Alerting;

/// <summary>
/// Debounces conditions into raise/clear events. A condition has to hold for <c>threshold</c>
/// consecutive updates before it is raised, and be absent for as many before it clears, so a
/// single odd poll (nvidia-smi timing out once) never produces a notification.
/// </summary>
/// <remarks>
/// Not thread-safe, deliberately: it has one owner, the loop that feeds it updates in the
/// order they happened.
/// </remarks>
/// <param name="time">When a condition counts as raised, for <see cref="Raised"/>. The system clock when omitted.</param>
public sealed class ConditionDebouncer(uint threshold, TimeProvider? time = null)
{
    private readonly uint _threshold = Math.Max(threshold, 1);
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>How many consecutive updates each not-yet-raised condition has held for.</summary>
    private readonly SortedDictionary<string, uint> _pending = new(StringComparer.Ordinal);

    /// <summary>Raised conditions, with when each was raised and how many consecutive updates it has been absent for.</summary>
    private readonly SortedDictionary<string, (Condition Condition, string Since, uint AbsentFor)> _raised = new(StringComparer.Ordinal);

    /// <summary>Whether <paramref name="kind"/> is raised: a condition with hysteresis needs to know.</summary>
    public bool IsRaised(string kind) => _raised.ContainsKey(kind);

    /// <summary>The severity <paramref name="kind"/> is raised at, or null: hysteresis per level needs it.</summary>
    public Severity? RaisedSeverity(string kind) => _raised.TryGetValue(kind, out var held) ? held.Condition.Severity : null;

    /// <summary>Every condition raised now, in key order: the verdict the page shows (ADR-019).</summary>
    public IEnumerable<Alert> Raised => _raised.Select(entry => new Alert
    {
        Kind = entry.Key,
        Severity = entry.Value.Condition.Severity,
        Message = entry.Value.Condition.Message,
        Since = entry.Value.Since,
    });

    /// <summary>Feeds one update's conditions in; returns what was raised, then what cleared.</summary>
    /// <param name="conditions">Every condition this update saw hold.</param>
    /// <param name="isComplete">
    /// False when the update could not judge every condition, because its source was unreadable.
    /// A condition it did not see is then unknown rather than gone: raised ones stay raised and
    /// pending ones keep their count. Without this, one failed UPS poll mid-outage would clear
    /// "on battery" and announce that mains power was back.
    /// </param>
    public List<NewEvent> Update(Conditions conditions, bool isComplete = true)
    {
        var events = new List<NewEvent>();

        foreach (var (kind, condition) in conditions)
        {
            if (_raised.TryGetValue(kind, out var held))
            {
                // One condition, one alert, at whatever level it has reached: a pool going
                // from 80 % to 90 % full is the same problem, worse, so it keeps its since. Worse
                // is news and is announced; easing is not, and shows in the verdict alone.
                if (condition.Severity != held.Condition.Severity)
                {
                    if (condition.Severity > held.Condition.Severity)
                    {
                        events.Add(new NewEvent(condition.Severity, kind, condition.Message));
                    }

                    held = held with { Condition = condition };
                }

                _raised[kind] = held with { AbsentFor = 0 };
                continue;
            }

            var seen = _pending.GetValueOrDefault(kind) + 1;
            if (seen >= _threshold)
            {
                _pending.Remove(kind);
                _raised[kind] = (condition, Rfc3339.From(_time.GetUtcNow()), 0);
                events.Add(new NewEvent(condition.Severity, kind, condition.Message));
            }
            else
            {
                _pending[kind] = seen;
            }
        }

        if (!isComplete)
        {
            return events;
        }

        // A condition must hold for consecutive updates; one gap starts the count again.
        foreach (var kind in _pending.Keys.Where(kind => !conditions.ContainsKey(kind)).ToList())
        {
            _pending.Remove(kind);
        }

        foreach (var (kind, (condition, since, absentFor)) in _raised.Where(entry => !conditions.ContainsKey(entry.Key)).ToList())
        {
            var absent = absentFor + 1;
            if (absent >= _threshold)
            {
                _raised.Remove(kind);
                events.Add(new NewEvent(Severity.Info, kind, condition.Cleared));
            }
            else
            {
                _raised[kind] = (condition, since, absent);
            }
        }

        return events;
    }
}
