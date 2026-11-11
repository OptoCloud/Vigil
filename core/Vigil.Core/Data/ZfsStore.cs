using Npgsql;
using NpgsqlTypes;
using Vigil.Core.Alerting;
using Vigil.Core.Protocol;

namespace Vigil.Core.Data;

/// <summary>
/// ZFS history (ADR-018): the scans, and vdev error counters when they move. A pool's state is
/// not here: its changes are conditions, and <c>events</c> records those.
/// </summary>
public sealed class ZfsStore(VigilDataSources databases)
{
    private static readonly string UpsertScan = SqlText.Load("upsert_zfs_scan");
    private static readonly string InsertVdevErrors = SqlText.Load("insert_zfs_vdev_errors");
    private static readonly string SelectLastScrubs = SqlText.Load("select_last_scrubs");

    /// <summary>Each pool's last completed scrub, for a restart to remember.</summary>
    public async Task<Dictionary<string, DateTimeOffset>> LastScrubsAsync(CancellationToken cancellationToken)
    {
        var scrubs = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        await using var command = databases.Writer.CreateCommand(SelectLastScrubs);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            scrubs[reader.GetString(0)] = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1));
        }

        return scrubs;
    }

    /// <summary>One poll's rows, together: a scan and the counters it moved are one moment.</summary>
    public async Task SaveAsync(string asOf, PoolUpdate update, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (update.Scans.Count == 0 && update.VdevErrors.Count == 0)
        {
            return;
        }

        await using var connection = await databases.Writer.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        foreach (var (pool, scan) in update.Scans)
        {
            await using var command = new NpgsqlCommand(UpsertScan, connection, transaction);
            command.Parameters.Add(Text(pool));
            command.Parameters.Add(Text(scan.Function));
            command.Parameters.Add(Text(scan.StartTime));
            command.Parameters.Add(Text(scan.EndTime));
            command.Parameters.Add(Text(scan.State));
            command.Parameters.Add(Big(scan.ExaminedBytes));
            command.Parameters.Add(Big(scan.Errors));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (update.VdevErrors.Count > 0)
        {
            var rows = update.VdevErrors;
            await using var command = new NpgsqlCommand(InsertVdevErrors, connection, transaction);
            command.Parameters.Add(Text(asOf));
            command.Parameters.Add(Texts(rows.Select(row => row.Pool)));
            command.Parameters.Add(Texts(rows.Select(row => row.VdevGuid)));
            command.Parameters.Add(Bigs(rows.Select(row => row.Read)));
            command.Parameters.Add(Bigs(rows.Select(row => row.Write)));
            command.Parameters.Add(Bigs(rows.Select(row => row.Checksum)));
            command.Parameters.Add(Bigs(rows.Select(row => row.Slow)));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static NpgsqlParameter Text(string? value) =>
        new() { Value = value is null ? DBNull.Value : value, NpgsqlDbType = NpgsqlDbType.Text };

    /// <summary>ZFS counts unsigned; the columns are bigint, which no real count outgrows.</summary>
    private static NpgsqlParameter Big(ulong? value) =>
        new() { Value = value is null ? DBNull.Value : (long)value.Value, NpgsqlDbType = NpgsqlDbType.Bigint };

    private static NpgsqlParameter Texts(IEnumerable<string> values) =>
        new() { Value = values.ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text };

    private static NpgsqlParameter Bigs(IEnumerable<ulong?> values) =>
        new() { Value = values.Select(value => (long?)value).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint };
}
