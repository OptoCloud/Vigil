using Vigil.Core.Alerting;
using Vigil.Core.Configuration;
using Vigil.Core.Protocol;

namespace Vigil.Core.Runtime;

/// <summary>
/// The runtime's ZFS state (ADR-018): the latest poll, the last readable one for diffing, each
/// pool's last completed scrub, and the debouncer for the pool conditions. Its own type because
/// the runtime is already near the size ceiling (docs/STYLE.md §2.1); like the runtime, it has
/// one owner and no locks.
/// </summary>
/// <remarks>
/// vigild repeats one ZFS poll unchanged in every 2-second snapshot until its next 60-second
/// poll, so the diff runs only when <see cref="ZfsStatus.AsOf"/> moves; diffing every snapshot
/// would compare a poll with itself. The conditions are judged on every tick instead, because
/// two of them are about time passing (a stale poll, an overdue scrub).
/// </remarks>
internal sealed class PoolWatch(VigilOptions options, TimeProvider time)
{
    /// <summary>
    /// One poll, unlike vigild's flaky-sensor threshold of three: zpool reports ZFS's own
    /// state, so a DEGRADED pool is a fact, not a bad read. The UPS is judged the same way.
    /// </summary>
    private const uint Threshold = 1;

    private readonly ConditionDebouncer _debouncer = new(Threshold, time);
    private readonly TimeSpan _scrubMaxAge = TimeSpan.FromDays(options.ZfsScrubMaxAgeDays);
    private readonly IReadOnlyDictionary<string, int> _expectedSpares = SpareCounts.Of(options);
    private readonly Dictionary<string, DateTimeOffset> _lastScrubs = new(StringComparer.Ordinal);

    private ZfsStatus? _latest;
    private ZfsStatus? _lastReadable;
    private string? _lastAsOf;

    public IEnumerable<Alert> Raised => _debouncer.Raised;

    /// <summary>Each pool's remembered history, as the <c>pools</c> message carries it.</summary>
    public IReadOnlyList<PoolHistory> History =>
        [.. (_lastReadable?.Pools ?? []).Select(pool => new PoolHistory
        {
            Name = pool.Name,
            LastScrub = PoolConditions.LastScrub(pool, _lastScrubs) is { } at ? Rfc3339.From(at) : null,
        })];

    /// <summary>Adds what the database remembers, keeping whichever scrub is newer.</summary>
    public void Remember(IReadOnlyDictionary<string, DateTimeOffset> lastScrubs)
    {
        foreach (var (pool, at) in lastScrubs)
        {
            Remember(pool, at);
        }
    }

    private void Remember(string pool, DateTimeOffset scrubbed)
    {
        if (!_lastScrubs.TryGetValue(pool, out var known) || scrubbed > known)
        {
            _lastScrubs[pool] = scrubbed;
        }
    }

    /// <summary>
    /// Takes one snapshot's ZFS report. Returns what a NEW poll means against the last readable
    /// one, or null when this is the poll already seen (or vigild does not report ZFS).
    /// </summary>
    public PoolUpdate? Observe(ZfsStatus? zfs)
    {
        _latest = zfs;
        if (zfs is null || zfs.AsOf == _lastAsOf)
        {
            return null;
        }

        _lastAsOf = zfs.AsOf;
        var update = PoolDiff.Diff(_lastReadable, zfs);
        if (zfs.IsAvailable)
        {
            _lastReadable = zfs;
            foreach (var pool in zfs.Pools)
            {
                if (PoolConditions.LastScrub(pool, _lastScrubs) is { } scrubbed)
                {
                    Remember(pool.Name, scrubbed);
                }
            }
        }

        return update;
    }

    /// <summary>Judges the pool conditions now; returns what was raised or cleared.</summary>
    public List<NewEvent> Evaluate()
    {
        var judgement = new PoolJudgement(time.GetUtcNow(), _lastScrubs, _scrubMaxAge, _expectedSpares, _debouncer.IsRaised);
        return _debouncer.Update(PoolConditions.Of(_latest, judgement), PoolConditions.IsComplete(_latest));
    }
}
