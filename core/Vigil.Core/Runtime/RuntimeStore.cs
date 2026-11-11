using Microsoft.Extensions.Options;
using Vigil.Core.Alerting;
using Vigil.Core.Configuration;
using Vigil.Core.Data;
using Vigil.Core.Protocol;

namespace Vigil.Core.Runtime;

/// <summary>
/// Every database operation the runtime performs. An interface so the runtime's tests can fake
/// the database (docs/STYLE.md §7) rather than need one, and in particular so "the database is
/// down" is a test case rather than a slow connect to a closed port.
/// </summary>
public interface IRuntimeStore
{
    /// <summary>
    /// Applies the schema, then loads what the runtime remembers across restarts: the first
    /// thing done with a database that has just become reachable, and what opens the runtime's
    /// gate to it.
    /// </summary>
    public Task<PreparedState> PrepareAsync(CancellationToken cancellationToken);

    /// <summary>One ZFS poll's history rows (<see cref="ZfsStore"/>).</summary>
    public Task SavePoolsAsync(string asOf, PoolUpdate update, CancellationToken cancellationToken);

    public Task SaveDriveAsync(DriveState drive, bool changed, CancellationToken cancellationToken);

    public Task InsertSamplesAsync(Snapshot snapshot, CancellationToken cancellationToken);

    public Task TouchInventoryAsync(Snapshot snapshot, CancellationToken cancellationToken);

    public Task InsertUpsSampleAsync(UpsReading reading, CancellationToken cancellationToken);

    public Task<EventRecord> InsertEventAsync(NewEvent newEvent, CancellationToken cancellationToken);
}

/// <summary>What the runtime remembers across a restart, loaded when the database becomes reachable.</summary>
/// <param name="Drives">Every drive's last good SMART state.</param>
/// <param name="LastScrubs">Each pool's last completed scrub.</param>
public sealed record PreparedState(IReadOnlyList<DriveState> Drives, IReadOnlyDictionary<string, DateTimeOffset> LastScrubs);

/// <summary>The real one: the data layer's writers, over the schema-owning role.</summary>
public sealed class DatabaseRuntimeStore(
    SchemaSetup schema,
    SampleWriter samples,
    DriveStore drives,
    ZfsStore zfs,
    EventStore events,
    IOptions<VigilOptions> options) : IRuntimeStore
{
    public async Task<PreparedState> PrepareAsync(CancellationToken cancellationToken)
    {
        await schema.ApplyAsync(TimeSpan.FromDays(options.Value.RawRetentionDays), cancellationToken).ConfigureAwait(false);
        return new PreparedState(
            await drives.LoadAsync(cancellationToken).ConfigureAwait(false),
            await zfs.LastScrubsAsync(cancellationToken).ConfigureAwait(false));
    }

    public Task SavePoolsAsync(string asOf, PoolUpdate update, CancellationToken cancellationToken) =>
        zfs.SaveAsync(asOf, update, cancellationToken);

    public Task SaveDriveAsync(DriveState drive, bool changed, CancellationToken cancellationToken) =>
        drives.SaveAsync(drive, changed, cancellationToken);

    public Task InsertSamplesAsync(Snapshot snapshot, CancellationToken cancellationToken) =>
        samples.InsertSamplesAsync(snapshot, cancellationToken);

    public Task TouchInventoryAsync(Snapshot snapshot, CancellationToken cancellationToken) =>
        samples.TouchInventoryAsync(snapshot, cancellationToken);

    public Task InsertUpsSampleAsync(UpsReading reading, CancellationToken cancellationToken) =>
        samples.InsertUpsSampleAsync(reading, cancellationToken);

    public Task<EventRecord> InsertEventAsync(NewEvent newEvent, CancellationToken cancellationToken) =>
        events.InsertAsync(newEvent.Severity, newEvent.Kind, newEvent.Message, cancellationToken);
}
