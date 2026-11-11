namespace Vigil.Core.Configuration;

/// <summary>
/// <c>ZFS_EXPECTED_SPARES</c>: how many hot spares each pool should have available, written
/// as <c>void:2</c> or <c>void:2,tank:1</c>.
/// </summary>
public static class SpareCounts
{
    /// <summary>
    /// Parses <paramref name="text"/>. Empty or unset is valid and checks no pool. On failure,
    /// <paramref name="problem"/> finishes the sentence "ZFS_EXPECTED_SPARES ...".
    /// </summary>
    public static bool TryParse(string? text, out IReadOnlyDictionary<string, int> counts, out string problem)
    {
        var parsed = new Dictionary<string, int>(StringComparer.Ordinal);
        counts = parsed;
        problem = string.Empty;

        foreach (var entry in (text ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = entry.Split(':', StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || parts[0].Length == 0
                || !int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var count)
                || count < 1)
            {
                problem = $"must be pool:count pairs separated by commas, such as void:2; '{entry}' is not one.";
                return false;
            }

            if (!parsed.TryAdd(parts[0], count))
            {
                problem = $"names pool '{parts[0]}' twice.";
                return false;
            }
        }

        return true;
    }

    /// <summary>The counts from an already-validated option.</summary>
    public static IReadOnlyDictionary<string, int> Of(VigilOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return TryParse(options.ZfsExpectedSpares, out var counts, out _) ? counts : new Dictionary<string, int>();
    }
}
