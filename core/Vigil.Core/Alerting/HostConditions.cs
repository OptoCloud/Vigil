using System.Globalization;
using Vigil.Core.Protocol;

namespace Vigil.Core.Alerting;

/// <summary>
/// The host's load, averaged over <c>HostWatch</c>'s window. Percentages; null where the host
/// does not report the figure (no swap, no PSI) or no reading in the window had it.
/// </summary>
/// <param name="MemoryPercent">RAM in use that is neither cache nor the ZFS ARC, of all RAM.</param>
/// <param name="SwapPercent">Swap in use, of all swap.</param>
/// <param name="MemoryStallPercent">The share of the time every task was stalled on memory (PSI "full").</param>
public sealed record HostSustained(double? MemoryPercent, double? SwapPercent, double? MemoryStallPercent);

/// <summary>
/// The host running out of memory. Judged on a sustained average, never one reading: a backup
/// or a guest starting fills RAM for a moment, and that is what RAM is for.
/// </summary>
/// <remarks>
/// CPU and I/O are recorded and charted (ADR-025) and deliberately not judged. A busy CPU is a
/// host doing its job, and a scrub holds I/O pressure above 50 % for hours on this kind of
/// machine, by design. Memory is different: there is no slow, only the OOM killer choosing a guest.
/// </remarks>
public static class HostConditions
{
    public const string Memory = "host-memory";
    public const string Swap = "host-swap";
    public const string MemoryPressure = "host-memory-pressure";

    /// <summary>Percent of RAM held by processes and guests. The ARC is left out: ZFS gives it back when asked.</summary>
    public const double MemoryWarn = 90;

    /// <inheritdoc cref="MemoryWarn"/>
    public const double MemoryCritical = 95;

    /// <summary>Percent of swap in use. Half of it gone means the host has been short of RAM for a while.</summary>
    public const double SwapWarn = 50;

    /// <summary>
    /// Percent of the time everything was stalled waiting for memory. Healthy is zero; a tenth
    /// of the time, sustained, is a host that is thrashing.
    /// </summary>
    public const double MemoryStallWarn = 10;

    /// <summary>Percentage points a figure has to fall below its limit before the limit lets go.</summary>
    public const double Hysteresis = 3;

    /// <param name="raised">The severity a kind is raised at now, or null.</param>
    public static Conditions Of(HostSustained sustained, Func<string, Severity?> raised)
    {
        ArgumentNullException.ThrowIfNull(sustained);
        ArgumentNullException.ThrowIfNull(raised);

        var conditions = new Conditions();
        double Threshold(double level, bool reached) => reached ? level - Hysteresis : level;

        if (sustained.MemoryPercent is { } memory)
        {
            var held = raised(Memory);
            var severity =
                memory >= Threshold(MemoryCritical, held == Severity.Critical) ? Severity.Critical
                : memory >= Threshold(MemoryWarn, held is not null) ? Severity.Warning
                : (Severity?)null;
            if (severity is { } level)
            {
                conditions[Memory] = new Condition(
                    level,
                    $"The host's memory is {Whole(memory)} % in use, not counting cache and the ZFS ARC. Past what is left, the kernel kills a process or a guest.",
                    $"The host's memory is below {Whole(MemoryWarn)} % in use again.");
            }
        }

        if (sustained.SwapPercent is { } swap && swap >= Threshold(SwapWarn, raised(Swap) is not null))
        {
            conditions[Swap] = new Condition(
                Severity.Warning,
                $"The host's swap is {Whole(swap)} % in use: it has been short of memory.",
                $"The host's swap is below {Whole(SwapWarn)} % in use again.");
        }

        if (sustained.MemoryStallPercent is { } stall && stall >= Threshold(MemoryStallWarn, raised(MemoryPressure) is not null))
        {
            conditions[MemoryPressure] = new Condition(
                Severity.Warning,
                $"The host is stalled on memory {Whole(stall)} % of the time: it is reclaiming or swapping instead of working.",
                "The host is no longer stalled on memory.");
        }

        return conditions;
    }

    private static string Whole(double value) => value.ToString("0", CultureInfo.InvariantCulture);
}
