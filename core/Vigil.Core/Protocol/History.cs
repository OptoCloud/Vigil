using System.Text.Json.Serialization;

namespace Vigil.Core.Protocol;

// What the dashboard adds on top of vigild's snapshot. Not yet in core/contract.json, which
// began as the live stream's half of the old Rust-generated file; until it moves there,
// HistoryQueriesTests pins every property name against what web/src/lib/types.ts declares.

/// <summary>How far back a chart goes, and at what resolution.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RangeKey>))]
public enum RangeKey
{
    [JsonStringEnumMemberName("1h")] OneHour,
    [JsonStringEnumMemberName("6h")] SixHours,
    [JsonStringEnumMemberName("24h")] OneDay,
    [JsonStringEnumMemberName("7d")] SevenDays,
    [JsonStringEnumMemberName("30d")] ThirtyDays,
    [JsonStringEnumMemberName("1y")] OneYear,
}

/// <summary>
/// One series of <c>[epochMillis, value]</c> points. An array pair rather than an object per
/// point: at a few hundred points across a dozen series the difference in payload is real, and
/// this is the shape the chart already reads.
/// </summary>
public sealed record HistoryResponse
{
    public required RangeKey Range { get; init; }

    /// <summary>Epoch milliseconds.</summary>
    public required long From { get; init; }

    public required long To { get; init; }
    public required int BucketSeconds { get; init; }

    /// <summary>
    /// Keyed by series id: a sensor id ("drive:&lt;wwn&gt;" follows a disk), a bay
    /// ("port:&lt;by-path&gt;" follows a location, whichever disk is in it), or "drives:max" /
    /// "memory:max" for the hottest of a group.
    /// </summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<double[]>> Temperatures { get; init; }

    /// <summary>Keyed by fan id.</summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<double[]>> Duties { get; init; }

    public required IReadOnlyDictionary<string, IReadOnlyList<double[]>> Rpms { get; init; }

    /// <summary>
    /// "charge" and "load" (%), "runtime" (minutes), "inputVoltage" (V). Empty without NUT.
    /// </summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<double[]>> Ups { get; init; }

    /// <summary>
    /// The host's load (ADR-025): "memoryUsed", "memoryCached", "arc" and "swapUsed" in bytes;
    /// "cpuBusy", "cpuIowait", "pressureCpu", "pressureMemory" and "pressureIo" in percent;
    /// "load1", "load5" and "load15". A series is absent until it has a value.
    /// </summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<double[]>> Host { get; init; }

    /// <summary>
    /// Bytes per second into the host, keyed "disk:&lt;name&gt;" (read), "pool:&lt;name&gt;" (the
    /// pool's disks, read) or "net:&lt;interface&gt;" (received).
    /// </summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<double[]>> TrafficIn { get; init; }

    /// <summary>Bytes per second out, under the same keys: written, or transmitted.</summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<double[]>> TrafficOut { get; init; }

    /// <summary>Percent of the time each "disk:&lt;name&gt;" had I/O in flight.</summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<double[]>> Busy { get; init; }

    /// <summary>Percent of each pool's raw size allocated, keyed by pool name.</summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<double[]>> PoolFill { get; init; }
}
