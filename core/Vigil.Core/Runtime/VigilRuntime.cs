using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Vigil.Core.Alerting;
using Vigil.Core.Clients;
using Vigil.Core.Configuration;
using Vigil.Core.Notifications;
using Vigil.Core.Protocol;

namespace Vigil.Core.Runtime;

/// <summary>
/// vigil-core's heart: one loop that owns all state. vigild's snapshots, the UPS readings and
/// new live subscribers arrive on one channel; history goes to Postgres, events to the log, the
/// database and ntfy, and every change to the live subscribers. One owner means no locks, and
/// the order of events is the order they happened in. Ported from the Rust core's runtime.rs.
/// </summary>
/// <remarks>
/// The schema is applied each time the database becomes reachable (<see cref="Data.SchemaSetup"/>).
/// The first fill of the rollups can take minutes, and the live view waits for it, as it did in
/// the Rust core: it happens once per database, not once per start.
/// </remarks>
public sealed class VigilRuntime
{
    /// <summary>How often the inventory tables are refreshed. They change when hardware does.</summary>
    private static readonly TimeSpan InventoryEvery = TimeSpan.FromSeconds(60);

    /// <summary>How often time-based conditions are judged when nothing arrives.</summary>
    private static readonly TimeSpan TickEvery = TimeSpan.FromSeconds(1);

    /// <summary>Three polls (6s at vigild's 2s cadence), so one odd read never notifies anyone.</summary>
    private const uint SnapshotThreshold = 3;

    private readonly IRuntimeStore _store;
    private readonly AlertRouter _router;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly DatabaseGate _database;
    private readonly LiveBroadcaster _live = new();
    private readonly TimeSpan _persistInterval;
    private readonly TimeSpan _daemonLostAfter;

    private Snapshot? _latest;
    private bool _daemonConnected;
    private (long Since, string Reason)? _daemonLostSince;
    private bool _daemonLostRaised;
    private readonly DriveTracking _drives;
    private readonly UpsTracking _ups;

    private readonly ConditionDebouncer _snapshotDebouncer;
    private readonly PoolWatch _pools;
    private IReadOnlyList<Alert> _alerts = [];
    private IReadOnlyList<PoolHistory> _poolHistory = [];
    private string? _daemonLostAt;
    private readonly HashSet<string> _knownSensorIds = new(StringComparer.Ordinal);
    private long? _lastPersisted;
    private long? _lastUpsPersisted;
    private long? _lastInventory;

    /// <param name="notifiers">Every way of reaching a person that is configured; empty is fine.</param>
    public VigilRuntime(VigilOptions options, IRuntimeStore store, IReadOnlyList<INotifier> notifiers, ILogger<VigilRuntime> logger, TimeProvider time)
    {
        _store = store;
        _router = new AlertRouter(notifiers);
        _snapshotDebouncer = new ConditionDebouncer(SnapshotThreshold, time);
        _pools = new PoolWatch(options, time);
        _drives = new DriveTracking(options, time);
        _logger = logger;
        _time = time;
        _database = new DatabaseGate(logger, time);
        _persistInterval = TimeSpan.FromSeconds(options.PersistIntervalSeconds);
        _daemonLostAfter = TimeSpan.FromSeconds(options.DaemonLostAfterSeconds);
        _ups = new UpsTracking(options, logger, time);
        _daemonLostSince = (time.GetTimestamp(), "not connected yet");
    }

    /// <summary>Runs until cancelled or until the input channel is completed.</summary>
    public async Task RunAsync(ChannelReader<RuntimeInput> inputs, CancellationToken cancellationToken)
    {
        try
        {
            while (await WaitForInputAsync(inputs, cancellationToken).ConfigureAwait(false))
            {
                // Everything already queued goes first, so the time-based checks in the tick judge
                // on the latest news: a "connected to vigild" that queued up while the database
                // was slow must be seen before an outage is declared.
                while (inputs.TryRead(out var input))
                {
                    await HandleAsync(input, cancellationToken).ConfigureAwait(false);
                }

                await TickAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _live.CloseAll();
        }
    }

    /// <summary>True when there is input or a tick is due; false once the channel is completed.</summary>
    private static async Task<bool> WaitForInputAsync(ChannelReader<RuntimeInput> inputs, CancellationToken cancellationToken)
    {
        using var tick = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        tick.CancelAfter(TickEvery);
        try
        {
            return await inputs.WaitToReadAsync(tick.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return true;
        }
    }

    internal int SubscriberCount => _live.Count;

    internal async Task HandleAsync(RuntimeInput input, CancellationToken cancellationToken)
    {
        switch (input)
        {
            case RuntimeInput.Daemon { Event: DaemonEvent.Received received }:
                await HandleSnapshotAsync(received.Snapshot, cancellationToken).ConfigureAwait(false);
                break;
            case RuntimeInput.Daemon { Event: DaemonEvent.Connected }:
                await HandleDaemonConnectionAsync(true, null, cancellationToken).ConfigureAwait(false);
                break;
            case RuntimeInput.Daemon { Event: DaemonEvent.Disconnected disconnected }:
                await HandleDaemonConnectionAsync(false, disconnected.Reason, cancellationToken).ConfigureAwait(false);
                break;
            case RuntimeInput.Ups ups:
                await HandleUpsAsync(ups.Poll, cancellationToken).ConfigureAwait(false);
                break;
            case RuntimeInput.Subscribe subscribe:
                _live.Subscribe(subscribe.Subscriber, CurrentState());
                break;
        }

        BroadcastVerdictIfChanged();
    }

    internal async Task TickAsync(CancellationToken cancellationToken)
    {
        // A daemon restart drops the stream for a second or two; only a lasting outage is news.
        if (_daemonLostSince is var (since, reason) && !_daemonLostRaised && _time.GetElapsedTime(since) >= _daemonLostAfter)
        {
            _daemonLostRaised = true;
            _daemonLostAt = Rfc3339.From(_time.GetUtcNow());
            await RaiseAsync(
                new NewEvent(
                    Severity.Critical,
                    "daemon-lost",
                    $"vigild is unreachable ({reason}). If it is not running, the fans are on BIOS control."),
                cancellationToken).ConfigureAwait(false);
        }

        // The unreadable and unguarded conditions depend on time, not only on the next poll.
        if (_ups.IsWaiting)
        {
            await RaiseAllAsync(_ups.Evaluate(), cancellationToken).ConfigureAwait(false);
        }

        // The pool conditions include two about time passing: a stale poll and an overdue scrub.
        await RaiseAllAsync(_pools.Evaluate(), cancellationToken).ConfigureAwait(false);

        // Last, because it may wait out a connect timeout; RunAsync handles what queued meanwhile first.
        if (_database.IsDue)
        {
            var prepared = await _database.TryOpenAsync("preparing the database", () => _store.PrepareAsync(cancellationToken))
                .ConfigureAwait(false);
            if (prepared is not null)
            {
                _drives.Remember(prepared.Drives);
                _pools.Remember(prepared.LastScrubs);
                _live.Broadcast(new DrivesMessage { Drives = _drives.List });
                await RaiseAllAsync(_drives.Evaluate(), cancellationToken).ConfigureAwait(false);
            }
        }

        BroadcastVerdictIfChanged();
    }

    private async Task HandleSnapshotAsync(Snapshot snapshot, CancellationToken cancellationToken)
    {
        var events = _snapshotDebouncer.Update(SnapshotConditions.In(snapshot, _knownSensorIds));
        _knownSensorIds.UnionWith(snapshot.Sensors.Select(sensor => sensor.Id));

        var drives = _drives.Observe(snapshot.DriveHealth, snapshot.Bays);
        if (drives is not null)
        {
            events.AddRange(drives.Events);
            events.AddRange(_drives.Evaluate());
        }

        var pools = _pools.Observe(snapshot.Zfs, snapshot.Bays);
        if (pools is not null)
        {
            events.AddRange(pools.Events);
        }

        events.AddRange(_pools.Evaluate());

        var now = _time.GetTimestamp();
        var persist = IsDue(_lastPersisted, _persistInterval);
        var inventory = IsDue(_lastInventory, InventoryEvery);
        if (_database.IsReady)
        {
            _lastPersisted = persist ? now : _lastPersisted;
            _lastInventory = inventory ? now : _lastInventory;
        }

        await _database.TryAsync("writing history", async () =>
        {
            foreach (var (state, changed) in drives?.ToSave ?? [])
            {
                await _store.SaveDriveAsync(state, changed, cancellationToken).ConfigureAwait(false);
            }

            if (persist)
            {
                await _store.InsertSamplesAsync(snapshot, cancellationToken).ConfigureAwait(false);
            }

            if (inventory)
            {
                await _store.TouchInventoryAsync(snapshot, cancellationToken).ConfigureAwait(false);
            }

            if (pools is not null && snapshot.Zfs is { } zfs)
            {
                await _store.SavePoolsAsync(zfs.AsOf, pools, cancellationToken).ConfigureAwait(false);
            }
        }).ConfigureAwait(false);

        _live.Broadcast(new SnapshotMessage { Snapshot = snapshot });
        _latest = snapshot;
        if (drives is { ToSave.Count: > 0 })
        {
            _live.Broadcast(new DrivesMessage { Drives = _drives.List });
        }

        await RaiseAllAsync(events, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleDaemonConnectionAsync(bool connected, string? reason, CancellationToken cancellationToken)
    {
        if (connected == _daemonConnected)
        {
            // Still down: keep the first reason, which is the one worth reporting.
            return;
        }

        _daemonConnected = connected;
        _live.Broadcast(new DaemonMessage { Connected = connected });

        if (!connected)
        {
            reason ??= "unknown reason";
            _logger.LogWarning("vigild connection lost: {Reason}", reason);
            _daemonLostSince = (_time.GetTimestamp(), reason);
            return;
        }

        _logger.LogInformation("connected to vigild");
        _daemonLostSince = null;
        _daemonLostAt = null;
        if (_daemonLostRaised)
        {
            _daemonLostRaised = false;
            await RaiseAsync(new NewEvent(Severity.Info, "daemon-lost", "vigild is reachable again."), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleUpsAsync(UpsPoll poll, CancellationToken cancellationToken)
    {
        if (_ups.Observe(poll) is { } reading && _database.IsReady && IsDue(_lastUpsPersisted, _persistInterval))
        {
            _lastUpsPersisted = _time.GetTimestamp();
            await _database.TryAsync("writing UPS history", () => _store.InsertUpsSampleAsync(reading, cancellationToken))
                .ConfigureAwait(false);
        }

        _live.Broadcast(new UpsMessage { Ups = _ups.State });
        await RaiseAllAsync(_ups.Evaluate(), cancellationToken).ConfigureAwait(false);
    }

    private async Task RaiseAllAsync(IEnumerable<NewEvent> events, CancellationToken cancellationToken)
    {
        foreach (var newEvent in events)
        {
            await RaiseAsync(newEvent, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RaiseAsync(NewEvent newEvent, CancellationToken cancellationToken)
    {
        var level = newEvent.Severity == Severity.Info ? LogLevel.Information : LogLevel.Warning;
        if (_logger.IsEnabled(level))
        {
            _logger.Log(level, "{Severity}: {Message}", newEvent.Severity, newEvent.Message);
        }

        var record = await _database.TryAsync("recording an event", () => _store.InsertEventAsync(newEvent, cancellationToken))
            .ConfigureAwait(false)
            ?? new EventRecord
            {
                // Negative ids never collide with the database's, and are unique enough for a page's list.
                Id = -_time.GetUtcNow().ToUnixTimeMilliseconds(),
                Ts = Rfc3339.From(_time.GetUtcNow()),
                Severity = newEvent.Severity,
                Kind = newEvent.Kind,
                Message = newEvent.Message,
            };

        _live.Broadcast(new EventMessage { Event = record });
        _router.Route(newEvent);
    }

    /// <summary>
    /// Every raised condition, worst first then oldest first: the page's verdict (ADR-019).
    /// vigild being unreachable is not a debounced condition, so it is added here.
    /// </summary>
    private List<Alert> Verdict()
    {
        IEnumerable<Alert> alerts = [.. _snapshotDebouncer.Raised, .. _ups.Raised, .. _pools.Raised, .. _drives.Raised];
        if (_daemonLostRaised && _daemonLostAt is { } since)
        {
            alerts = alerts.Append(new Alert
            {
                Kind = "daemon-lost",
                Severity = Severity.Critical,
                Message = "vigild is unreachable. If it is not running, the fans are on BIOS control.",
                Since = since,
            });
        }

        return [.. alerts.OrderByDescending(alert => alert.Severity).ThenBy(alert => alert.Since, StringComparer.Ordinal)];
    }

    /// <summary>Sends the verdict and the pools' history to the page, when either has changed.</summary>
    private void BroadcastVerdictIfChanged()
    {
        var alerts = Verdict();
        if (!alerts.SequenceEqual(_alerts))
        {
            _alerts = alerts;
            _live.Broadcast(new AlertsMessage { Alerts = alerts });
        }

        var history = _pools.History;
        if (!history.SequenceEqual(_poolHistory))
        {
            _poolHistory = history;
            _live.Broadcast(new PoolsMessage { Pools = history });
        }
    }

    private bool IsDue(long? last, TimeSpan interval) => last is not { } at || _time.GetElapsedTime(at) >= interval;

    private IEnumerable<LiveMessage> CurrentState()
    {
        yield return new DaemonMessage { Connected = _daemonConnected };
        if (_latest is not null)
        {
            yield return new SnapshotMessage { Snapshot = _latest };
        }

        yield return new DrivesMessage { Drives = _drives.List };
        yield return new UpsMessage { Ups = _ups.State };
        yield return new AlertsMessage { Alerts = _alerts };
        yield return new PoolsMessage { Pools = _poolHistory };
    }
}
