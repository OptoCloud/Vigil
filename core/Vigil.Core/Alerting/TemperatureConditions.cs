using System.Globalization;
using Vigil.Core.Protocol;

namespace Vigil.Core.Alerting;

/// <summary>
/// A sensor that is too hot. vigild's curves answer heat with air; this is for when that was
/// not enough, or when there was no air to answer with: a dead fan is one alert, and the drives
/// it cooled reaching 55 °C is the one that says how long there is.
/// </summary>
public static class TemperatureConditions
{
    /// <summary>
    /// °C a sensor has to fall below a limit before the limit lets go, the same as the UPS
    /// battery's: a reading that sits on the line would otherwise raise and clear all day.
    /// </summary>
    public const double CoolingMargin = 2;

    public static string KindOf(string sensorId) => $"temperature:{sensorId}";

    /// <summary>
    /// Adds a condition to <paramref name="conditions"/> for every sensor in
    /// <paramref name="snapshot"/> at or over its category's limit.
    /// </summary>
    /// <param name="raised">The severity a kind is raised at now, or null: each level holds until the sensor is <see cref="CoolingMargin"/> under it.</param>
    public static void AddTo(Conditions conditions, Snapshot snapshot, TemperatureLimits limits, Func<string, Severity?> raised)
    {
        ArgumentNullException.ThrowIfNull(conditions);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(raised);

        foreach (var sensor in snapshot.Sensors)
        {
            if (limits.For(sensor.Category) is not { } limit)
            {
                continue;
            }

            var kind = KindOf(sensor.Id);
            var held = raised(kind);
            var cleared = $"Sensor '{sensor.Id}' ({sensor.Label}) is back under {Whole(limit.Warn)} °C.";

            if (!sensor.IsAvailable || sensor.CelsiusOrNull is not { } celsius)
            {
                // Not knowing is not "cooled down" (invariant 3): a hot sensor that stops
                // answering stays raised, at the level it had, until it is read again.
                if (held is { } level)
                {
                    conditions[kind] = new Condition(level, $"Sensor '{sensor.Id}' ({sensor.Label}) was over its limit and can no longer be read.", cleared);
                }

                continue;
            }

            double Threshold(double at, bool reached) => reached ? at - CoolingMargin : at;
            var severity =
                celsius >= Threshold(limit.Critical, held == Severity.Critical) ? Severity.Critical
                : celsius >= Threshold(limit.Warn, held is not null) ? Severity.Warning
                : (Severity?)null;
            if (severity is { } now)
            {
                var at = now == Severity.Critical ? limit.Critical : limit.Warn;
                conditions[kind] = new Condition(
                    now,
                    $"Sensor '{sensor.Id}' ({sensor.Label}) is at {Whole(celsius)} °C ({(now == Severity.Critical ? "critical" : "warning")} at {Whole(at)} °C).",
                    cleared);
            }
        }
    }

    private static string Whole(double value) => value.ToString("0", CultureInfo.InvariantCulture);
}
