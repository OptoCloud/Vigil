using Vigil.Core.Alerting;
using Vigil.Core.Protocol;

namespace Vigil.Core.Runtime;

/// <summary>
/// The runtime's memory of the host's load, for judging it: the last few minutes of readings,
/// averaged, because a condition here is about what is sustained (<see cref="HostConditions"/>).
/// One owner, no locks.
/// </summary>
internal sealed class HostWatch(TimeProvider time)
{
    /// <summary>What "sustained" means. Long enough to sit out a guest booting or a backup starting.</summary>
    internal static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How much of <see cref="Window"/> the readings must span before they are judged. Not all
    /// of it, since readings arrive every couple of seconds and never land on its edges.
    /// </summary>
    private static readonly TimeSpan MinimumSpan = Window * 0.9;

    /// <summary>Three in a row, as for every condition: the average moves slowly, so this only orders events.</summary>
    private const uint Threshold = 3;

    private readonly Queue<(long At, double? Memory, double? Swap, double? MemoryStall)> _readings = new();
    private readonly ConditionDebouncer _debouncer = new(Threshold, time);

    public IEnumerable<Alert> Raised => _debouncer.Raised;

    /// <summary>Takes one new reading and judges the window. Returns what was raised or cleared.</summary>
    public List<NewEvent> Observe(HostLoad load)
    {
        var now = time.GetTimestamp();
        _readings.Enqueue((
            now,
            load.Memory is { TotalBytes: > 0 } memory ? Unreclaimable(memory) * 100.0 / memory.TotalBytes : null,
            load.Memory is { SwapTotalBytes: > 0 } swap ? swap.SwapUsedBytes * 100.0 / swap.SwapTotalBytes : null,
            load.Pressure?.MemoryFullPercent));
        while (time.GetElapsedTime(_readings.Peek().At, now) > Window)
        {
            _readings.Dequeue();
        }

        // Too little to call sustained (just started, or vigild was away): nothing is judged,
        // and nothing raised is cleared. Not knowing is not "back to normal".
        if (time.GetElapsedTime(_readings.Peek().At, now) < MinimumSpan)
        {
            return _debouncer.Update(new Conditions(), isComplete: false);
        }

        var sustained = new HostSustained(
            Mean(_readings.Select(reading => reading.Memory)),
            Mean(_readings.Select(reading => reading.Swap)),
            Mean(_readings.Select(reading => reading.MemoryStall)));
        return _debouncer.Update(HostConditions.Of(sustained, _debouncer.RaisedSeverity));
    }

    /// <summary>What processes and guests hold: used, less the ARC, which is counted as used and is not.</summary>
    private static double Unreclaimable(MemoryLoad memory) => memory.UsedBytes - Math.Min(memory.UsedBytes, memory.ArcBytes ?? 0);

    private static double? Mean(IEnumerable<double?> values)
    {
        var known = values.OfType<double>().ToList();
        return known.Count == 0 ? null : known.Average();
    }
}
