using Npgsql;
using NpgsqlTypes;
using Vigil.Core.Protocol;

namespace Vigil.Core.Data;

/// <summary>
/// A drive's last GOOD health result, plus a history row whenever something moved.
/// </summary>
/// <remarks>
/// vigild reports only what its latest poll saw and never wakes a sleeping drive, so a drive is
/// regularly unavailable with nothing to say. This table is what survives those gaps.
/// </remarks>
public sealed class DriveStore(VigilDataSources databases)
{
    private static readonly string SelectDrives = SqlText.Load("select_drives");
    private static readonly string UpsertDrive = SqlText.Load("upsert_drive");
    private static readonly string InsertHealthHistory = SqlText.Load("insert_drive_health_history");
    private static readonly string SelectHealthHistory = SqlText.Load("select_drive_health_history");

    public async Task<List<DriveState>> LoadAsync(CancellationToken cancellationToken)
    {
        var drives = new List<DriveState>();

        await using var command = databases.Reader.CreateCommand(SelectDrives);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            drives.Add(new DriveState
            {
                Wwn = reader.GetString(0),
                Port = reader.IsDBNull(1) ? null : reader.GetString(1),
                Passed = reader.IsDBNull(2) ? null : reader.GetBoolean(2),
                ReallocatedSectorCount = Count(reader, 3),
                PendingSectorCount = Count(reader, 4),
                PowerOnHours = Count(reader, 5),
                SourcePath = reader.GetString(6),
                // Epoch milliseconds, formatted here, so no date handling crosses the boundary
                // as text in either direction.
                AsOf = Rfc3339.From(DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(7))),
                SerialNumber = reader.IsDBNull(8) ? null : reader.GetString(8),
                ReportedUncorrect = Count(reader, 9),
                CommandTimeout = Count(reader, 10),
                UdmaCrcErrorCount = Count(reader, 11),
                LastSelfTestPassed = reader.IsDBNull(12) ? null : reader.GetBoolean(12),
                LastSelfTestHours = Count(reader, 13),
            });
        }

        return drives;
    }

    /// <summary>
    /// One drive's recorded health changes, newest first, at most <paramref name="limit"/>.
    /// Read through the SELECT-only role. An unknown drive is an empty list, not an error.
    /// </summary>
    public async Task<List<DriveHealthRecord>> HistoryAsync(string wwn, int limit, CancellationToken cancellationToken)
    {
        await using var command = databases.Reader.CreateCommand(SelectHealthHistory);
        command.Parameters.Add(Text(wwn));
        command.Parameters.Add(new() { Value = limit, NpgsqlDbType = NpgsqlDbType.Integer });

        var records = new List<DriveHealthRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            records.Add(new DriveHealthRecord
            {
                Ts = Rfc3339.From(DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0))),
                Passed = reader.IsDBNull(1) ? null : reader.GetBoolean(1),
                ReallocatedSectorCount = Count(reader, 2),
                PendingSectorCount = Count(reader, 3),
                PowerOnHours = Count(reader, 4),
                ReportedUncorrect = Count(reader, 5),
                CommandTimeout = Count(reader, 6),
                UdmaCrcErrorCount = Count(reader, 7),
                LastSelfTestPassed = reader.IsDBNull(8) ? null : reader.GetBoolean(8),
            });
        }

        return records;
    }

    /// <summary>
    /// Stores a drive's new last-good state, and a history row if <paramref name="changed"/>
    /// (anything but power-on hours moved).
    /// </summary>
    public async Task SaveAsync(DriveState drive, bool changed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(drive);

        await using var connection = await databases.Writer.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        // Both statements or neither: a history row without the state it describes, or a state
        // whose change was never recorded, would each be a lie about what the drive did.
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var upsert = new NpgsqlCommand(UpsertDrive, connection, transaction))
        {
            AddDriveParameters(upsert, drive);
            await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (changed)
        {
            await using var history = new NpgsqlCommand(InsertHealthHistory, connection, transaction);
            history.Parameters.Add(Text(drive.AsOf));
            history.Parameters.Add(Text(drive.Wwn));
            history.Parameters.Add(Nullable(drive.Passed, NpgsqlDbType.Boolean));
            history.Parameters.Add(Big(drive.ReallocatedSectorCount));
            history.Parameters.Add(Big(drive.PendingSectorCount));
            history.Parameters.Add(Big(drive.PowerOnHours));
            history.Parameters.Add(Big(drive.ReportedUncorrect));
            history.Parameters.Add(Big(drive.CommandTimeout));
            history.Parameters.Add(Big(drive.UdmaCrcErrorCount));
            history.Parameters.Add(Nullable(drive.LastSelfTestPassed, NpgsqlDbType.Boolean));
            await history.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>upsert_drive's parameters, in its order.</summary>
    private static void AddDriveParameters(NpgsqlCommand command, DriveState drive)
    {
        command.Parameters.Add(Text(drive.Wwn));
        command.Parameters.Add(Nullable(drive.Port, NpgsqlDbType.Text));
        command.Parameters.Add(Nullable(drive.Passed, NpgsqlDbType.Boolean));
        command.Parameters.Add(Big(drive.ReallocatedSectorCount));
        command.Parameters.Add(Big(drive.PendingSectorCount));
        command.Parameters.Add(Big(drive.PowerOnHours));
        command.Parameters.Add(Text(drive.SourcePath));
        command.Parameters.Add(Text(drive.AsOf));
        command.Parameters.Add(Nullable(drive.SerialNumber, NpgsqlDbType.Text));
        command.Parameters.Add(Big(drive.ReportedUncorrect));
        command.Parameters.Add(Big(drive.CommandTimeout));
        command.Parameters.Add(Big(drive.UdmaCrcErrorCount));
        command.Parameters.Add(Nullable(drive.LastSelfTestPassed, NpgsqlDbType.Boolean));
        command.Parameters.Add(Big(drive.LastSelfTestHours));
    }

    /// <summary>SMART reports these unsigned; the columns are bigint.</summary>
    private static ulong? Count(NpgsqlDataReader reader, int column) =>
        reader.IsDBNull(column) ? null : (ulong)reader.GetInt64(column);

    private static NpgsqlParameter Big(ulong? value) =>
        new() { Value = value is null ? DBNull.Value : (long)value.Value, NpgsqlDbType = NpgsqlDbType.Bigint };

    private static NpgsqlParameter Text(string value) => new() { Value = value, NpgsqlDbType = NpgsqlDbType.Text };

    private static NpgsqlParameter Nullable<T>(T? value, NpgsqlDbType type) =>
        new() { Value = value is null ? DBNull.Value : value, NpgsqlDbType = type };
}
