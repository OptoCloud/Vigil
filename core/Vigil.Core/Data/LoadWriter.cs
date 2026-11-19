using Npgsql;
using NpgsqlTypes;
using Vigil.Core.Protocol;

namespace Vigil.Core.Data;

/// <summary>
/// The host-load writes (ADR-025): one row of memory, CPU and pressure, the traffic of every
/// disk, pool and interface, and each pool's fill. Beside <see cref="SampleWriter"/> rather than
/// in it, because these are written from vigil-core's own rates rather than from a snapshot.
/// </summary>
public sealed class LoadWriter(VigilDataSources databases)
{
    private static readonly string InsertHostSample = SqlText.Load("insert_host_sample");
    private static readonly string InsertTrafficSamples = SqlText.Load("insert_traffic_samples");
    private static readonly string InsertPoolSamples = SqlText.Load("insert_pool_samples");

    /// <summary>The ids history stores traffic under; the page reads them back by the same names.</summary>
    public static string DiskId(string deviceName) => $"disk:{deviceName}";

    public static string PoolId(string pool) => $"pool:{pool}";

    public static string InterfaceId(string name) => $"net:{name}";

    public async Task InsertHostSampleAsync(HostLoad load, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(load);

        await using var connection = await databases.Writer.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var memory = load.Memory;
        await using (var command = new NpgsqlCommand(InsertHostSample, connection, transaction))
        {
            command.Parameters.Add(Text(load.AsOf));
            command.Parameters.Add(Bytes(memory?.UsedBytes));
            command.Parameters.Add(Bytes(memory?.CachedBytes));
            command.Parameters.Add(Bytes(memory?.ArcBytes));
            command.Parameters.Add(Bytes(memory?.SwapUsedBytes));
            command.Parameters.Add(Real(load.Cpu?.BusyPercent));
            command.Parameters.Add(Real(load.Cpu?.IowaitPercent));
            command.Parameters.Add(Real(load.Load?.One));
            command.Parameters.Add(Real(load.Load?.Five));
            command.Parameters.Add(Real(load.Load?.Fifteen));
            command.Parameters.Add(Real(load.Pressure?.CpuPercent));
            command.Parameters.Add(Real(load.Pressure?.MemoryPercent));
            command.Parameters.Add(Real(load.Pressure?.IoPercent));
            command.Parameters.Add(Bytes(memory?.TotalBytes));
            command.Parameters.Add(Bytes(memory?.SwapTotalBytes));
            command.Parameters.Add(Bytes(memory?.ArcMaxBytes));
            command.Parameters.Add(Real(load.Pressure?.CpuFullPercent));
            command.Parameters.Add(Real(load.Pressure?.MemoryFullPercent));
            command.Parameters.Add(Real(load.Pressure?.IoFullPercent));
            command.Parameters.Add(new NpgsqlParameter { Value = (object?)load.UptimeSeconds ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Double });
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var rows = TrafficRows(load);
        if (rows.Count > 0)
        {
            await using var command = new NpgsqlCommand(InsertTrafficSamples, connection, transaction);
            command.Parameters.Add(Text(load.AsOf));
            command.Parameters.Add(Array(rows.Select(row => row.Id).ToArray(), NpgsqlDbType.Text));
            command.Parameters.Add(Array(rows.Select(row => (float)row.In).ToArray(), NpgsqlDbType.Real));
            command.Parameters.Add(Array(rows.Select(row => (float)row.Out).ToArray(), NpgsqlDbType.Real));
            command.Parameters.Add(Array(rows.Select(row => (float?)row.Busy).ToArray(), NpgsqlDbType.Real));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Every traffic row in one load, under the ids history keeps them by.</summary>
    internal static List<(string Id, double In, double Out, double? Busy)> TrafficRows(HostLoad load) =>
    [
        .. load.Disks.Select(disk => (DiskId(disk.DeviceName), disk.ReadBytesPerSecond, disk.WrittenBytesPerSecond, (double?)disk.BusyPercent)),
        .. load.Pools.Select(pool => (PoolId(pool.Name), pool.ReadBytesPerSecond, pool.WrittenBytesPerSecond, (double?)null)),
        .. load.Interfaces.Select(link => (InterfaceId(link.Name), link.ReceivedBytesPerSecond, link.TransmittedBytesPerSecond, (double?)null)),
    ];

    /// <summary>Each pool zpool could size, from one ZFS poll.</summary>
    public async Task InsertPoolSamplesAsync(ZfsStatus zfs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(zfs);

        var pools = zfs.Pools.Where(pool => pool.SizeBytes is > 0 && pool.AllocatedBytes is not null).ToList();
        if (pools.Count == 0)
        {
            return;
        }

        await using var command = databases.Writer.CreateCommand(InsertPoolSamples);
        command.Parameters.Add(Text(zfs.AsOf));
        command.Parameters.Add(Array(pools.Select(pool => pool.Name).ToArray(), NpgsqlDbType.Text));
        command.Parameters.Add(Array(pools.Select(pool => ToBigint(pool.SizeBytes!.Value)).ToArray(), NpgsqlDbType.Bigint));
        command.Parameters.Add(Array(pools.Select(pool => ToBigint(pool.AllocatedBytes!.Value)).ToArray(), NpgsqlDbType.Bigint));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The columns are bigint. No memory or pool size reaches 2^63 bytes, but one that somehow
    /// did is stored as the largest bigint rather than wrapping to a negative.
    /// </summary>
    private static long ToBigint(ulong value) => value > long.MaxValue ? long.MaxValue : (long)value;

    private static NpgsqlParameter Text(string value) => new() { Value = value, NpgsqlDbType = NpgsqlDbType.Text };

    private static NpgsqlParameter Bytes(ulong? value) =>
        new() { Value = value is { } bytes ? ToBigint(bytes) : DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint };

    private static NpgsqlParameter Real(double? value) =>
        new() { Value = value is { } number ? (float)number : DBNull.Value, NpgsqlDbType = NpgsqlDbType.Real };

    private static NpgsqlParameter Array<T>(T[] values, NpgsqlDbType element) => new() { Value = values, NpgsqlDbType = NpgsqlDbType.Array | element };
}
