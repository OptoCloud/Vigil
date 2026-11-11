using System.Globalization;
using Vigil.Core.Protocol;

namespace Vigil.Core.Alerting;

/// <summary>What pool conditions are judged against, beyond the poll itself.</summary>
/// <param name="Now">The time to judge ages at.</param>
/// <param name="LastScrubs">Each pool's last completed scrub that vigil-core remembers, from <c>zfs_scans</c>.</param>
/// <param name="ScrubMaxAge">How old the last completed scrub may get, <c>ZFS_SCRUB_MAX_AGE_DAYS</c>.</param>
/// <param name="ExpectedSpares">Hot spares each pool should have AVAIL, <c>ZFS_EXPECTED_SPARES</c>.</param>
/// <param name="RaisedSeverity">The severity a condition is raised at now, or null: capacity has hysteresis per level.</param>
/// <param name="Bays">Each port's bay name (<see cref="BayNames"/>), so a failed disk's alert says where it is.</param>
public sealed record PoolJudgement(
    DateTimeOffset Now,
    IReadOnlyDictionary<string, DateTimeOffset> LastScrubs,
    TimeSpan ScrubMaxAge,
    IReadOnlyDictionary<string, int> ExpectedSpares,
    Func<string, Severity?> RaisedSeverity,
    IReadOnlyDictionary<string, string> Bays);

/// <summary>
/// The ZFS problems that hold right now (ADR-018, docs/CLEANUP.md 7.3), as conditions the
/// debouncer raises once and clears once. The one-time changes are <see cref="PoolDiff"/>.
/// </summary>
public static class PoolConditions
{
    /// <summary>
    /// A poll older than this means vigild's ZFS thread has stopped answering: five of its
    /// 60-second polls missed, which is not jitter.
    /// </summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);

    /// <summary>Capacity levels, percent: ZFS slows as free space fragments, sharply past 90.</summary>
    public const double CapacityWarn = 80;

    public const double CapacityCritical = 90;

    /// <summary>
    /// How far below a level a raised capacity condition must fall to clear. Without it, a pool
    /// at 80.0 % would raise and clear with every few gigabytes written and deleted.
    /// </summary>
    public const double CapacityHysteresis = 2;

    public const string Unreadable = "zfs-unreadable";

    /// <summary>
    /// Every ZFS problem in <paramref name="zfs"/>. Nothing at all when vigild does not report
    /// ZFS. When zpool could not be read, only that is known; the caller must then tell the
    /// debouncer the update was incomplete, so a DEGRADED pool is not declared healthy by a
    /// failed poll (the lesson <see cref="UpsConditions"/> learned for "on battery").
    /// </summary>
    public static Conditions Of(ZfsStatus? zfs, PoolJudgement judgement)
    {
        ArgumentNullException.ThrowIfNull(judgement);

        var conditions = new Conditions();
        if (zfs is null)
        {
            return conditions;
        }

        var age = PoolDiff.TryParse(zfs.AsOf, out var asOf) ? judgement.Now - asOf : TimeSpan.MaxValue;
        if (!zfs.IsAvailable || age > StaleAfter)
        {
            conditions[Unreadable] = new Condition(
                Severity.Warning,
                zfs.IsAvailable
                    ? "vigild's ZFS poll has not answered for over 5 minutes, so pool state is unknown."
                    : "vigild cannot read zpool, so pool state is unknown. vigild's journal says why.",
                "ZFS pool state is readable again.");
        }

        if (!zfs.IsAvailable)
        {
            return conditions;
        }

        foreach (var pool in zfs.Pools)
        {
            Levels(pool, judgement.Bays, conditions);
            Capacity(pool, judgement, conditions);
            ScrubAge(pool, judgement, conditions);
            Spares(pool, judgement, conditions);
        }

        return conditions;
    }

    /// <summary>Whether this check can judge every condition: false when zpool gave no answer.</summary>
    public static bool IsComplete(ZfsStatus? zfs) => zfs is null || zfs.IsAvailable;

    private static void Levels(PoolStatus pool, IReadOnlyDictionary<string, string> bays, Conditions conditions)
    {
        if (pool.State != "ONLINE")
        {
            conditions[$"zfs-pool:{pool.Name}"] = new Condition(
                Severity.Critical,
                $"Pool {pool.Name} is {pool.State}.",
                $"Pool {pool.Name} is ONLINE again.");
        }

        foreach (var vdev in pool.Vdevs)
        {
            // AVAIL is a hot spare ready to stand in; INUSE is one standing in for a failed disk.
            if (vdev.State is "ONLINE" or "AVAIL")
            {
                continue;
            }

            var why = vdev.Aux is { } aux ? $" ({aux})" : string.Empty;
            var role = vdev.State == "INUSE" ? "hot spare, standing in for a failed disk" : $"{vdev.State}{why}";
            var name = $"{vdev.Name}{BayNames.Suffix(bays, vdev.Port)}";
            conditions[$"zfs-vdev:{pool.Name}:{vdev.Guid ?? vdev.Name}"] = new Condition(
                Severity.Critical,
                $"{name} in pool {pool.Name} is {role}.",
                $"{name} in pool {pool.Name} is back to normal.");
        }

        if (pool.ErrorCount is > 0)
        {
            conditions[$"zfs-data-errors:{pool.Name}"] = new Condition(
                Severity.Critical,
                $"Pool {pool.Name} has {pool.ErrorCount} permanent data errors: blocks ZFS could not repair. `zpool status -v {pool.Name}` lists the files.",
                $"Pool {pool.Name} reports no permanent data errors any more.");
        }
    }

    private static void Capacity(PoolStatus pool, PoolJudgement judgement, Conditions conditions)
    {
        if (pool.SizeBytes is not > 0 || pool.AllocatedBytes is not { } allocated)
        {
            return;
        }

        var percent = allocated * 100.0 / pool.SizeBytes.Value;

        // One condition whose severity follows the level, so a filling pool is one alert that
        // worsens rather than two. Each level holds CapacityHysteresis below itself once reached.
        var kind = $"zfs-capacity:{pool.Name}";
        var raised = judgement.RaisedSeverity(kind);
        double Threshold(double level, bool reached) => reached ? level - CapacityHysteresis : level;
        var severity =
            percent >= Threshold(CapacityCritical, raised == Severity.Critical) ? Severity.Critical
            : percent >= Threshold(CapacityWarn, raised is not null) ? Severity.Warning
            : (Severity?)null;
        if (severity is { } level)
        {
            var at = level == Severity.Critical ? CapacityCritical : CapacityWarn;
            conditions[kind] = new Condition(
                level,
                $"Pool {pool.Name} is {percent.ToString("0", CultureInfo.InvariantCulture)} % full (past {at} % ZFS slows down{(level == Severity.Critical ? " sharply" : string.Empty)}).",
                $"Pool {pool.Name} is below {CapacityWarn} % full again.");
        }
    }

    /// <summary>
    /// The newest completed scrub either source knows of. A pool with no known scrub is not
    /// judged: without a database, "never scrubbed" and "not remembered" look the same.
    /// </summary>
    private static void ScrubAge(PoolStatus pool, PoolJudgement judgement, Conditions conditions)
    {
        if (LastScrub(pool, judgement.LastScrubs) is not { } last || judgement.Now - last <= judgement.ScrubMaxAge)
        {
            return;
        }

        conditions[$"zfs-scrub-age:{pool.Name}"] = new Condition(
            Severity.Warning,
            $"Pool {pool.Name} was last scrubbed {(int)(judgement.Now - last).TotalDays} days ago; scrubs are due every {(int)judgement.ScrubMaxAge.TotalDays}. Is the scrub timer running?",
            $"Pool {pool.Name} has been scrubbed.");
    }

    /// <summary>The newest completed scrub: what zpool shows now, or what vigil-core remembers.</summary>
    public static DateTimeOffset? LastScrub(PoolStatus pool, IReadOnlyDictionary<string, DateTimeOffset> remembered)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(remembered);

        DateTimeOffset? current = pool.Scan is { Function: "SCRUB", State: "FINISHED" } scan && PoolDiff.TryParse(scan.EndTime, out var end)
            ? end
            : null;
        DateTimeOffset? stored = remembered.TryGetValue(pool.Name, out var at) ? at : null;
        return current > stored || stored is null ? current : stored;
    }

    private static void Spares(PoolStatus pool, PoolJudgement judgement, Conditions conditions)
    {
        if (!judgement.ExpectedSpares.TryGetValue(pool.Name, out var expected))
        {
            return;
        }

        var available = pool.Vdevs.Count(vdev => vdev.Class == "spare" && vdev.State == "AVAIL");
        if (available < expected)
        {
            conditions[$"zfs-spares:{pool.Name}"] = new Condition(
                Severity.Warning,
                $"Pool {pool.Name} has {available} of its {expected} hot spares available.",
                $"Pool {pool.Name} has all {expected} hot spares available again.");
        }
    }
}
