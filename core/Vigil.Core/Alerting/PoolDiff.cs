using System.Globalization;
using Vigil.Core.Protocol;

namespace Vigil.Core.Alerting;

/// <summary>A scan worth a row in <c>zfs_scans</c>: new, or changed since the last poll.</summary>
public sealed record ScanRow(string Pool, PoolScan Scan);

/// <summary>A vdev's error counters, worth a row in <c>zfs_vdev_errors</c> because one of them moved.</summary>
public sealed record VdevErrorRow(string Pool, string VdevGuid, ulong? Read, ulong? Write, ulong? Checksum, ulong? Slow);

/// <summary>What one ZFS poll means against the one before it: rows to keep and one-time events.</summary>
public sealed record PoolUpdate(IReadOnlyList<ScanRow> Scans, IReadOnlyList<VdevErrorRow> VdevErrors, IReadOnlyList<NewEvent> Events)
{
    public static readonly PoolUpdate Empty = new([], [], []);
}

/// <summary>
/// The ZFS changes that are events rather than conditions: counters that grew and scans that
/// started or finished. The levels (a pool not ONLINE, a full pool) are <see cref="PoolConditions"/>.
/// <see cref="DriveHealthDiff"/> is the same job for SMART.
/// </summary>
public static class PoolDiff
{
    private const string Resilver = "RESILVER";
    private const string Scrub = "SCRUB";
    private const string Scanning = "SCANNING";
    private const string Finished = "FINISHED";

    /// <summary>
    /// Compares one poll with the last READABLE one before it. A poll zpool could not answer
    /// says nothing, so it yields nothing. With no previous poll, the rows that record the
    /// starting point are kept but nothing is announced: what was already true when vigil-core
    /// started is history, not news, and the conditions report anything still wrong.
    /// </summary>
    public static PoolUpdate Diff(ZfsStatus? previous, ZfsStatus current)
    {
        ArgumentNullException.ThrowIfNull(current);

        if (!current.IsAvailable)
        {
            return PoolUpdate.Empty;
        }

        var scans = new List<ScanRow>();
        var vdevErrors = new List<VdevErrorRow>();
        var events = new List<NewEvent>();

        foreach (var pool in current.Pools)
        {
            var before = previous?.Pools.FirstOrDefault(other => other.Name == pool.Name);

            if (pool.Scan is { StartTime: not null } scan && before?.Scan != scan)
            {
                scans.Add(new ScanRow(pool.Name, scan));
                if (previous is not null)
                {
                    events.AddRange(ScanEvents(pool.Name, before?.Scan, scan));
                }
            }

            foreach (var vdev in pool.Vdevs)
            {
                if (vdev.Guid is not { } guid)
                {
                    continue;
                }

                var was = before?.Vdevs.FirstOrDefault(other => other.Guid == guid);
                var row = new VdevErrorRow(pool.Name, guid, vdev.ReadErrors, vdev.WriteErrors, vdev.ChecksumErrors, vdev.SlowIos);
                if (was is null ? HasAny(row) : !SameCounts(was, vdev))
                {
                    vdevErrors.Add(row);
                }

                if (previous is not null && was is not null && Grew(was, vdev) is { Length: > 0 } growth)
                {
                    events.Add(new NewEvent(
                        Severity.Warning,
                        $"zfs-vdev-errors:{pool.Name}:{guid}",
                        $"{vdev.Name} in pool {pool.Name} has new errors: {growth}."));
                }
            }
        }

        return new PoolUpdate(scans, vdevErrors, events);
    }

    private static IEnumerable<NewEvent> ScanEvents(string pool, PoolScan? before, PoolScan scan)
    {
        var sameScan = before is not null && before.Function == scan.Function && before.StartTime == scan.StartTime;

        if (scan is { Function: Resilver, State: Scanning } && !(sameScan && before!.State == Scanning))
        {
            yield return new NewEvent(
                Severity.Warning,
                $"zfs-resilver:{pool}",
                $"Pool {pool} started resilvering: a disk is being rebuilt from redundancy.");
        }

        if (scan.State != Finished || (sameScan && before!.State == Finished))
        {
            yield break;
        }

        var errors = scan.Errors ?? 0;
        if (scan.Function == Resilver)
        {
            var took = Duration(scan) is { } duration ? $" in {duration}" : string.Empty;
            yield return errors > 0
                ? new NewEvent(Severity.Warning, $"zfs-resilver:{pool}", $"Pool {pool} finished resilvering{took}, with {errors} errors.")
                : new NewEvent(Severity.Info, $"zfs-resilver:{pool}", $"Pool {pool} finished resilvering{took}, with no errors.");
        }
        else if (scan.Function == Scrub && errors > 0)
        {
            yield return new NewEvent(Severity.Warning, $"zfs-scrub:{pool}", $"The scrub of pool {pool} finished with {errors} errors.");
        }
    }

    /// <summary>"4 h 24 min", or null when either end is missing.</summary>
    private static string? Duration(PoolScan scan)
    {
        if (!TryParse(scan.StartTime, out var start) || !TryParse(scan.EndTime, out var end))
        {
            return null;
        }

        var took = end - start;
        return took.TotalHours >= 1 ? $"{(int)took.TotalHours} h {took.Minutes} min" : $"{Math.Max(1, (int)took.TotalMinutes)} min";
    }

    internal static bool TryParse(string? rfc3339, out DateTimeOffset at) =>
        DateTimeOffset.TryParse(rfc3339, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out at);

    private static bool HasAny(VdevErrorRow row) => (row.Read ?? 0) + (row.Write ?? 0) + (row.Checksum ?? 0) + (row.Slow ?? 0) > 0;

    private static bool SameCounts(VdevStatus a, VdevStatus b) =>
        a.ReadErrors == b.ReadErrors && a.WriteErrors == b.WriteErrors && a.ChecksumErrors == b.ChecksumErrors && a.SlowIos == b.SlowIos;

    /// <summary>
    /// "read 0 → 3, checksum 1 → 4" for each counter that went UP. A counter going down is
    /// <c>zpool clear</c>, not news. Slow I/Os are recorded but not announced: they are latency,
    /// and a busy pool has them.
    /// </summary>
    private static string Grew(VdevStatus was, VdevStatus now)
    {
        var parts = new List<string>();
        void Check(string name, ulong? before, ulong? after)
        {
            if (after is { } a && a > (before ?? 0))
            {
                parts.Add($"{name} {before ?? 0} → {a}");
            }
        }

        Check("read", was.ReadErrors, now.ReadErrors);
        Check("write", was.WriteErrors, now.WriteErrors);
        Check("checksum", was.ChecksumErrors, now.ChecksumErrors);
        return string.Join(", ", parts);
    }
}
