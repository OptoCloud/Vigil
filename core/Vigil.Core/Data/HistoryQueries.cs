using System.Globalization;
using Npgsql;
using NpgsqlTypes;
using Vigil.Core.Protocol;

namespace Vigil.Core.Data;

/// <summary>
/// The chart history queries, ported from vigil-web's <c>history.ts</c>.
/// </summary>
/// <remarks>
/// Every range is bucketed in SQL down to a few hundred points per series, so a one-year chart
/// costs the browser no more than a one-hour one. Each range reads the coarsest level that still
/// has its resolution: raw samples while they exist, then vigil-core's TimescaleDB continuous
/// aggregates. Reads go through the SELECT-only role (docs/SECURITY.md §3.4).
/// </remarks>
public sealed class HistoryQueries(VigilDataSources databases)
{
    /// <summary>Which level a range reads. The table name comes from here and nowhere else.</summary>
    private enum Source
    {
        Raw,
        Minute,
        Hour,
    }

    private sealed record RangeSpec(int Seconds, int BucketSeconds, Source Source);

    private static readonly Dictionary<RangeKey, RangeSpec> Ranges = new()
    {
        // Raw samples are kept RAW_RETENTION_DAYS (30) and compressed after 7, so the two
        // shortest ranges read them directly; everything longer reads a rollup.
        [RangeKey.OneHour] = new(3_600, 10, Source.Raw),
        [RangeKey.SixHours] = new(21_600, 60, Source.Raw),
        [RangeKey.OneDay] = new(86_400, 300, Source.Minute),
        [RangeKey.SevenDays] = new(604_800, 1_800, Source.Minute),
        [RangeKey.ThirtyDays] = new(2_592_000, 7_200, Source.Hour),
        [RangeKey.OneYear] = new(31_536_000, 86_400, Source.Hour),
    };

    public static int BucketSecondsFor(RangeKey range) => Ranges[range].BucketSeconds;

    /// <param name="upsName">The UPS whose history to include, or null without NUT.</param>
    public async Task<HistoryResponse> QueryAsync(RangeKey range, string? upsName, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var spec = Ranges[range];
        var from = now.AddSeconds(-spec.Seconds);
        var bucket = $"{spec.BucketSeconds} seconds";

        var temperatures = new Dictionary<string, IReadOnlyList<double[]>>(StringComparer.Ordinal);
        await ReadAsync(
            QueryFor("history_sensors", spec.Source, "sensor"),
            bucket,
            from,
            null,
            reader =>
            {
                if (reader.IsDBNull(2))
                {
                    return;
                }

                Append(temperatures, reader.GetString(0), Millis(reader, 1), Round(reader.GetDouble(2), 2));
            },
            cancellationToken);

        var duties = new Dictionary<string, IReadOnlyList<double[]>>(StringComparer.Ordinal);
        var rpms = new Dictionary<string, IReadOnlyList<double[]>>(StringComparer.Ordinal);
        await ReadAsync(
            QueryFor("history_fans", spec.Source, "fan"),
            bucket,
            from,
            null,
            reader =>
            {
                var id = reader.GetString(0);
                var at = Millis(reader, 1);

                if (!reader.IsDBNull(2))
                {
                    Append(duties, id, at, Round(reader.GetDouble(2), 1));
                }

                // A header whose tach is unreadable has a duty but no rpm, and must not appear
                // in the rpm chart as a zero.
                if (!reader.IsDBNull(3))
                {
                    Append(rpms, id, at, Math.Round(reader.GetDouble(3), MidpointRounding.AwayFromZero));
                }
            },
            cancellationToken);

        var ups = new Dictionary<string, IReadOnlyList<double[]>>(StringComparer.Ordinal);
        if (upsName is { Length: > 0 })
        {
            await ReadAsync(
                QueryFor("history_ups", spec.Source, "ups"),
                bucket,
                from,
                upsName,
                reader =>
                {
                    var at = Millis(reader, 0);
                    AppendIfPresent(ups, "charge", reader, 1, at, 1);
                    AppendIfPresent(ups, "load", reader, 2, at, 1);
                    // Minutes, because that is what the chart's axis is in.
                    if (!reader.IsDBNull(3))
                    {
                        Append(ups, "runtime", at, Round(reader.GetDouble(3) / 60, 1));
                    }

                    AppendIfPresent(ups, "inputVoltage", reader, 4, at, 1);
                },
                cancellationToken);

            await ReadAsync(
                QueryFor("history_ups_detail", spec.Source, "ups_detail"),
                bucket,
                from,
                upsName,
                reader =>
                {
                    var at = Millis(reader, 0);
                    for (var column = 0; column < UpsDetailColumns.Length; column++)
                    {
                        AppendIfPresent(ups, UpsDetailColumns[column].Key, reader, column + 1, at, UpsDetailColumns[column].Digits);
                    }
                },
                cancellationToken);
        }

        var load = await QueryLoadAsync(spec.Source, bucket, from, cancellationToken).ConfigureAwait(false);

        // Thirteen drives and two DIMMs would blow through what colour can distinguish, so each
        // group is charted as one line: its hottest member in each bucket.
        temperatures["drives:max"] = HottestOf(temperatures, "drive:");
        temperatures["memory:max"] = HottestOf(temperatures, "dimm:");

        return new HistoryResponse
        {
            Range = range,
            From = from.ToUnixTimeMilliseconds(),
            To = now.ToUnixTimeMilliseconds(),
            BucketSeconds = spec.BucketSeconds,
            Temperatures = temperatures,
            Duties = duties,
            Rpms = rpms,
            Ups = ups,
            Host = load.Host,
            TrafficIn = load.TrafficIn,
            TrafficOut = load.TrafficOut,
            Busy = load.Busy,
            PoolFill = load.PoolFill,
        };
    }

    private sealed record LoadHistory(
        Dictionary<string, IReadOnlyList<double[]>> Host,
        Dictionary<string, IReadOnlyList<double[]>> TrafficIn,
        Dictionary<string, IReadOnlyList<double[]>> TrafficOut,
        Dictionary<string, IReadOnlyList<double[]>> Busy,
        Dictionary<string, IReadOnlyList<double[]>> PoolFill);

    /// <summary>The host's columns, in the order the host queries select them after the bucket.</summary>
    private static readonly (string Key, int Digits)[] HostColumns =
    [
        ("memoryUsed", 0), ("memoryCached", 0), ("arc", 0), ("swapUsed", 0), ("cpuBusy", 1), ("cpuIowait", 1),
        ("load1", 2), ("load5", 2), ("load15", 2), ("pressureCpu", 1), ("pressureMemory", 1), ("pressureIo", 1),
        ("memoryTotal", 0), ("swapTotal", 0), ("arcMax", 0), ("pressureCpuFull", 1), ("pressureMemoryFull", 1), ("pressureIoFull", 1),
    ];

    /// <summary>The UPS's later readings (ADR-026), in the order their queries select them after the bucket.</summary>
    private static readonly (string Key, int Digits)[] UpsDetailColumns =
    [
        ("batteryTemperature", 1), ("outputFrequency", 2), ("outputCurrent", 2), ("monitors", 0),
    ];

    /// <summary>The host's load, its traffic and its pools' fill (ADR-025).</summary>
    private async Task<LoadHistory> QueryLoadAsync(Source source, string bucket, DateTimeOffset from, CancellationToken cancellationToken)
    {
        var history = new LoadHistory(Series(), Series(), Series(), Series(), Series());

        await ReadAsync(QueryFor("history_host", source, "host"), bucket, from, null, reader =>
        {
            var at = Millis(reader, 0);
            for (var column = 0; column < HostColumns.Length; column++)
            {
                AppendIfPresent(history.Host, HostColumns[column].Key, reader, column + 1, at, HostColumns[column].Digits);
            }
        }, cancellationToken).ConfigureAwait(false);

        await ReadAsync(QueryFor("history_traffic", source, "traffic"), bucket, from, null, reader =>
        {
            var (id, at) = (reader.GetString(0), Millis(reader, 1));
            AppendIfPresent(history.TrafficIn, id, reader, 2, at, 0);
            AppendIfPresent(history.TrafficOut, id, reader, 3, at, 0);
            AppendIfPresent(history.Busy, id, reader, 4, at, 1);
        }, cancellationToken).ConfigureAwait(false);

        await ReadAsync(QueryFor("history_pools", source, "pool"), bucket, from, null, reader =>
            AppendIfPresent(history.PoolFill, reader.GetString(0), reader, 2, Millis(reader, 1), 2), cancellationToken).ConfigureAwait(false);

        return history;
    }

    private static Dictionary<string, IReadOnlyList<double[]>> Series() => new(StringComparer.Ordinal);

    /// <summary>
    /// The query for a series at a level, with the rollup's table name substituted in.
    /// </summary>
    /// <remarks>
    /// sql-literal-ok: the only thing interpolated is a table name, and it comes from an
    /// exhaustive switch over <see cref="Source"/> — a closed set of compile-time constants,
    /// never from a request (docs/STYLE.md §3.3). A continuous aggregate cannot be named by a
    /// bound parameter, so there is no alternative.
    /// </remarks>
    private static string QueryFor(string query, Source source, string prefix)
    {
        if (source == Source.Raw)
        {
            return SqlText.Load($"{query}_raw");
        }

        var table = source switch
        {
            Source.Minute => $"{prefix}_1m",
            Source.Hour => $"{prefix}_1h",
            _ => throw new ArgumentOutOfRangeException(nameof(source), source, "not a rollup level"),
        };

        return SqlText.Load($"{query}_rollup").Replace("{table}", table, StringComparison.Ordinal);
    }

    private async Task ReadAsync(
        string sql,
        string bucket,
        DateTimeOffset from,
        string? upsName,
        Action<NpgsqlDataReader> onRow,
        CancellationToken cancellationToken)
    {
        await using var command = databases.Reader.CreateCommand(sql);
        command.Parameters.Add(new NpgsqlParameter { Value = bucket, NpgsqlDbType = NpgsqlDbType.Text });
        command.Parameters.Add(new NpgsqlParameter { Value = from, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        if (upsName is not null)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = upsName, NpgsqlDbType = NpgsqlDbType.Text });
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            onRow(reader);
        }
    }

    /// <summary>The hottest member of a group per bucket, e.g. one "drives" line instead of thirteen.</summary>
    private static List<double[]> HottestOf(Dictionary<string, IReadOnlyList<double[]>> series, string prefix)
    {
        var hottest = new Dictionary<double, double>();
        foreach (var (id, points) in series)
        {
            if (!id.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var point in points)
            {
                if (!hottest.TryGetValue(point[0], out var current) || point[1] > current)
                {
                    hottest[point[0]] = point[1];
                }
            }
        }

        return hottest.OrderBy(entry => entry.Key).Select(entry => new[] { entry.Key, entry.Value }).ToList();
    }

    private static void Append(Dictionary<string, IReadOnlyList<double[]>> series, string id, double at, double value)
    {
        if (!series.TryGetValue(id, out var points))
        {
            points = new List<double[]>();
            series[id] = points;
        }

        ((List<double[]>)points).Add([at, value]);
    }

    private static void AppendIfPresent(
        Dictionary<string, IReadOnlyList<double[]>> series,
        string id,
        NpgsqlDataReader reader,
        int column,
        double at,
        int digits)
    {
        if (!reader.IsDBNull(column))
        {
            Append(series, id, at, Round(reader.GetDouble(column), digits));
        }
    }

    private static double Millis(NpgsqlDataReader reader, int column) =>
        new DateTimeOffset(reader.GetDateTime(column), TimeSpan.Zero).ToUnixTimeMilliseconds();

    /// <summary>
    /// Away from zero, matching the JavaScript this was ported from: .NET's default is banker's
    /// rounding, which would disagree with the TypeScript implementation on every halfway value.
    /// </summary>
    private static double Round(double value, int digits) => Math.Round(value, digits, MidpointRounding.AwayFromZero);
}
