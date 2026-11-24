using System.Globalization;
using Vigil.Core.Protocol;

namespace Vigil.Core.Alerting;

/// <summary>Where one kind of sensor is too hot, in °C: a warning, and above it a critical.</summary>
public readonly record struct TemperatureLimit(double Warn, double Critical);

/// <summary>
/// <c>TEMPERATURE_LIMITS</c>: the limit for each sensor category, written
/// <c>drive:50/60</c> or <c>drive:50/60,cpu:85/95</c>. A category not named keeps its default.
/// </summary>
/// <remarks>
/// By category and not by sensor: the fan curves are what treat one drive differently from
/// another, and they are vigild's. This is the line past which the curves have lost.
/// </remarks>
public sealed class TemperatureLimits
{
    /// <summary>No sensor vigil reads is meant to run hotter than this, so a limit above it is a typo.</summary>
    public const double Highest = 120;

    /// <summary>
    /// Each category's wire name and default. Chosen above what the fan curves reach at full
    /// duty and below what the part's maker allows: a disk is rated to 60 °C, a DDR4 DIMM to
    /// 85 °C, and an LSI SAS3008's own alarm is at 80 °C.
    /// </summary>
    private static readonly (string Name, SensorCategory Category, TemperatureLimit Default)[] Categories =
    [
        ("cpu", SensorCategory.Cpu, new(85, 95)),
        ("boardAmbient", SensorCategory.BoardAmbient, new(50, 60)),
        ("drive", SensorCategory.Drive, new(50, 60)),
        ("gpu", SensorCategory.Gpu, new(85, 95)),
        ("memory", SensorCategory.Memory, new(70, 85)),
        ("hba", SensorCategory.Hba, new(75, 85)),
    ];

    private readonly Dictionary<SensorCategory, TemperatureLimit> _limits;

    private TemperatureLimits(Dictionary<SensorCategory, TemperatureLimit> limits) => _limits = limits;

    public static TemperatureLimits Defaults { get; } = new(Categories.ToDictionary(entry => entry.Category, entry => entry.Default));

    /// <summary>Null for a category vigild adds before this list learns of it: it is then not judged.</summary>
    public TemperatureLimit? For(SensorCategory category) => _limits.TryGetValue(category, out var limit) ? limit : null;

    /// <summary>
    /// Parses <paramref name="text"/> over the defaults. Empty or unset is valid. On failure,
    /// <paramref name="problem"/> finishes the sentence "TEMPERATURE_LIMITS ...".
    /// </summary>
    public static bool TryParse(string? text, out TemperatureLimits limits, out string problem)
    {
        var parsed = Categories.ToDictionary(entry => entry.Category, entry => entry.Default);
        var named = new HashSet<string>(StringComparer.Ordinal);
        limits = new TemperatureLimits(parsed);
        problem = string.Empty;

        foreach (var entry in (text ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = entry.Split(':', '/');
            if (parts.Length != 3 || !Degrees(parts[1], out var warn) || !Degrees(parts[2], out var critical))
            {
                problem = $"must be category:warn/critical entries separated by commas, such as drive:50/60; '{entry}' is not one.";
                return false;
            }

            var category = Array.Find(Categories, known => known.Name == parts[0].Trim());
            if (category.Name is null)
            {
                problem = $"names '{parts[0].Trim()}', which is not one of {string.Join(", ", Categories.Select(known => known.Name))}.";
                return false;
            }

            if (warn >= critical)
            {
                problem = $"sets {category.Name}'s warning at or above its critical; the warning comes first.";
                return false;
            }

            if (!named.Add(category.Name))
            {
                problem = $"names {category.Name} twice.";
                return false;
            }

            parsed[category.Category] = new TemperatureLimit(warn, critical);
        }

        return true;
    }

    /// <summary>The limits from an already-validated option.</summary>
    public static TemperatureLimits Parse(string? text) =>
        TryParse(text, out var limits, out var problem) ? limits : throw new InvalidOperationException($"TEMPERATURE_LIMITS {problem}");

    private static bool Degrees(string text, out double degrees) =>
        double.TryParse(text, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite, CultureInfo.InvariantCulture, out degrees)
        && degrees is > 0 and <= Highest;
}
