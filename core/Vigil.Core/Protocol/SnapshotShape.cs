namespace Vigil.Core.Protocol;

/// <summary>
/// What the JSON reader cannot promise about a snapshot it accepted (docs/SECURITY.md §4.3).
/// </summary>
/// <remarks>
/// <c>RespectNullableAnnotations</c> refuses a null where a property is declared non-null, but
/// says nothing about a list's elements, and the reader turns a number too large for a double
/// into an infinity, which cannot be written back out as JSON. Either would throw on the
/// runtime's loop, the one thing that must keep running, and the daemon would send the same
/// snapshot again after the restart. So a snapshot with either is discarded where it arrives.
/// </remarks>
public static class SnapshotShape
{
    /// <summary>Why <paramref name="snapshot"/> cannot be used, or null when it can.</summary>
    public static string? Problem(Snapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (HasNull(snapshot.Sensors) || HasNull(snapshot.Fans) || HasNull(snapshot.DriveHealth) || HasNull(snapshot.Bays))
        {
            return "a list holds a null where an entry belongs";
        }

        if (snapshot.Zfs is { } zfs && (HasNull(zfs.Pools) || zfs.Pools.Any(pool => HasNull(pool.Vdevs))))
        {
            return "the pool list holds a null where an entry belongs";
        }

        if (snapshot.Sensors.FirstOrDefault(sensor => sensor.CelsiusOrNull is { } celsius && !double.IsFinite(celsius)) is { } sensor)
        {
            return $"sensor '{sensor.Id}' reports a temperature that is not a finite number";
        }

        return null;
    }

    private static bool HasNull<T>(IReadOnlyList<T> items)
        where T : class
    {
        foreach (var item in items)
        {
            if (item is null)
            {
                return true;
            }
        }

        return false;
    }
}
