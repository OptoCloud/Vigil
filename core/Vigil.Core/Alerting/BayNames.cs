using Vigil.Core.Protocol;

namespace Vigil.Core.Alerting;

/// <summary>
/// "8-bay slot 6" for a port, from vigild's <c>[[bays]]</c>, so an alert says which disk to
/// pull and not only what the disk is called. Empty when the port is in no bay, or unknown.
/// </summary>
public static class BayNames
{
    /// <summary>Each configured port's bay name. vigild refuses a port in two bays, so the first wins only in theory.</summary>
    public static IReadOnlyDictionary<string, string> Of(IReadOnlyList<Bay> bays)
    {
        ArgumentNullException.ThrowIfNull(bays);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var bay in bays)
        {
            names.TryAdd(bay.Port, $"{bay.Cage} slot {bay.Slot}");
        }

        return names;
    }

    /// <summary>" (8-bay slot 6)", or empty: ready to append to a disk's name.</summary>
    public static string Suffix(IReadOnlyDictionary<string, string> bays, string? port) =>
        port is not null && bays.TryGetValue(port, out var bay) ? $" ({bay})" : string.Empty;
}
