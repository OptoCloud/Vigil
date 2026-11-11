using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Vigil.Core.Clients;
using Vigil.Core.Configuration;
using Vigil.Core.Protocol;
using Vigil.Core.Runtime;
using Xunit;

namespace Vigil.Core.Tests;

/// <summary>
/// The first four are ported from the Rust core's runtime.rs tests; the rest pin the database gate,
/// which the Rust tests could not reach without a database.
/// </summary>
public sealed class VigilRuntimeTests
{
    private readonly FakeRuntimeStore _store = new() { Failure = FakeRuntimeStore.ConnectionLost() };
    private readonly ManualTime _time = new();

    private VigilRuntime Runtime(double daemonLostAfterSeconds = 20, string? nutHost = null) => new(
        new VigilOptions { PersistIntervalSeconds = 10, DaemonLostAfterSeconds = daemonLostAfterSeconds, NutHost = nutHost, NutUps = "apc" },
        _store,
        [],
        NullLogger<VigilRuntime>.Instance,
        _time);

    private static Snapshot Snapshot(bool stalled) => new()
    {
        TimestampUtc = "2026-10-01T12:00:00.000Z",
        Sensors = [],
        Fans = [new FanStatus { Id = "drive-cage", DutyPercent = 60, Rpm = 0, Mode = PwmMode.Manual, Stalled = stalled }],
        DriveHealth = [],
        ControlLoopHealthy = true,
    };

    private static RuntimeInput.Daemon Received(Snapshot snapshot) => new RuntimeInput.Daemon(new DaemonEvent.Received(snapshot));

    private static RuntimeInput.Daemon Connected => new RuntimeInput.Daemon(new DaemonEvent.Connected());

    private static RuntimeInput.Daemon Disconnected(string reason) => new RuntimeInput.Daemon(new DaemonEvent.Disconnected(reason));

    private static async Task<ChannelReader<string>> Subscribe(VigilRuntime runtime, int capacity = 64)
    {
        var subscriber = Channel.CreateBounded<string>(capacity);
        await runtime.HandleAsync(new RuntimeInput.Subscribe(subscriber.Writer), CancellationToken.None);
        return subscriber.Reader;
    }

    /// <summary>Everything queued for a subscriber right now.</summary>
    private static List<string> Drain(ChannelReader<string> subscriber)
    {
        var messages = new List<string>();
        while (subscriber.TryRead(out var json))
        {
            messages.Add(json);
        }

        return messages;
    }

    [Fact]
    public async Task ASubscriberGetsTheCurrentStateThenChangesWithoutADatabase()
    {
        var runtime = Runtime();
        await runtime.HandleAsync(Connected, CancellationToken.None);
        await runtime.HandleAsync(Received(Snapshot(false)), CancellationToken.None);

        var subscriber = await Subscribe(runtime);
        var first = Drain(subscriber);
        // daemon, snapshot, drives, ups, alerts, pools.
        Assert.Equal(6, first.Count);
        Assert.Contains("""
            "type":"daemon","connected":true
            """, first[0], StringComparison.Ordinal);
        Assert.StartsWith("""{"type":"snapshot" """.TrimEnd(), first[1], StringComparison.Ordinal);
        Assert.Contains("""
            "enabled":false,"name":"apc"
            """, first[3], StringComparison.Ordinal);

        // Three stalled polls raise one critical event, broadcast even with no database.
        for (var poll = 0; poll < 3; poll++)
        {
            await runtime.HandleAsync(Received(Snapshot(true)), CancellationToken.None);
        }

        var events = Drain(subscriber).Where(json => json.Contains("""
            "type":"event"
            """, StringComparison.Ordinal)).ToList();
        var raised = Assert.Single(events);
        Assert.Contains("""
            "severity":"critical"
            """, raised, StringComparison.Ordinal);
        Assert.Contains("fan-stalled:drive-cage", raised, StringComparison.Ordinal);

        // Not from the database, so the id is negative and cannot collide with one that is.
        Assert.Contains("""
            "id":-
            """, raised, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASubscriberThatCannotKeepUpIsDroppedAndItsStreamEnded()
    {
        var runtime = Runtime();
        // Room for the state on connect (daemon, drives, ups, alerts, pools: no snapshot yet), and nothing more.
        var subscriber = await Subscribe(runtime, capacity: 5);
        Assert.Equal(1, runtime.SubscriberCount);

        await runtime.HandleAsync(Received(Snapshot(false)), CancellationToken.None);

        Assert.Equal(0, runtime.SubscriberCount);
        Drain(subscriber);
        Assert.True(subscriber.Completion.IsCompleted);
    }

    [Fact]
    public async Task NewsAlreadyQueuedIsHandledBeforeAnOutageIsJudged()
    {
        var inputs = Channel.CreateUnbounded<RuntimeInput>();
        var subscriber = Channel.CreateBounded<string>(64);
        inputs.Writer.TryWrite(new RuntimeInput.Subscribe(subscriber.Writer));
        inputs.Writer.TryWrite(Connected);
        inputs.Writer.Complete();

        await Runtime(daemonLostAfterSeconds: 0).RunAsync(inputs.Reader, CancellationToken.None);

        Assert.DoesNotContain(Drain(subscriber.Reader), json => json.Contains("daemon-lost", StringComparison.Ordinal));

        // And a runtime that stops ends every stream, so shutdown does not wait on open pages.
        Assert.True(subscriber.Reader.Completion.IsCompleted);
    }

    [Fact]
    public async Task ALastingDaemonOutageIsRaisedOnceAndClearedOnReconnect()
    {
        var runtime = Runtime(daemonLostAfterSeconds: 0);
        var subscriber = await Subscribe(runtime);
        Drain(subscriber);

        await runtime.HandleAsync(Disconnected("connection refused"), CancellationToken.None);
        await runtime.TickAsync(CancellationToken.None);
        await runtime.TickAsync(CancellationToken.None);

        var lost = Assert.Single(
            Drain(subscriber),
            json => json.Contains("daemon-lost", StringComparison.Ordinal) && json.Contains("\"type\":\"event\"", StringComparison.Ordinal));
        Assert.Contains("vigild is unreachable (not connected yet)", lost, StringComparison.Ordinal);

        await runtime.HandleAsync(Connected, CancellationToken.None);
        Assert.Contains(Drain(subscriber), json => json.Contains("vigild is reachable again.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithTheDatabaseUpHistoryIsWrittenOncePerPersistIntervalAndEventsGetItsIds()
    {
        _store.Failure = null;
        var runtime = Runtime();
        await runtime.TickAsync(CancellationToken.None);
        var subscriber = await Subscribe(runtime);

        await runtime.HandleAsync(Received(Snapshot(false)), CancellationToken.None);
        await runtime.HandleAsync(Received(Snapshot(false)), CancellationToken.None);
        Assert.Equal(1, _store.CountOf("insert-samples"));
        Assert.Equal(1, _store.CountOf("touch-inventory"));

        _time.Advance(TimeSpan.FromSeconds(10));
        await runtime.HandleAsync(Received(Snapshot(true)), CancellationToken.None);
        Assert.Equal(2, _store.CountOf("insert-samples"));
        Assert.Equal(1, _store.CountOf("touch-inventory"));

        await runtime.HandleAsync(Received(Snapshot(true)), CancellationToken.None);
        await runtime.HandleAsync(Received(Snapshot(true)), CancellationToken.None);
        var raised = Assert.Single(Drain(subscriber), json => json.Contains("""
            "type":"event"
            """, StringComparison.Ordinal));
        Assert.Contains("""
            "id":1,
            """, raised, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AQueryErrorKeepsTheDatabaseButALostConnectionClosesItUntilTheRetry()
    {
        _store.Failure = null;
        var runtime = Runtime();
        await runtime.TickAsync(CancellationToken.None);

        // The server answered with an error: the next write is still attempted.
        _store.Failure = FakeRuntimeStore.QueryFailed();
        await runtime.HandleAsync(Received(Snapshot(false)), CancellationToken.None);
        _store.Failure = null;
        _time.Advance(TimeSpan.FromSeconds(10));
        await runtime.HandleAsync(Received(Snapshot(false)), CancellationToken.None);
        Assert.Equal(2, _store.CountOf("insert-samples"));

        // The connection is gone: nothing more is attempted until the retry is due.
        _store.Failure = FakeRuntimeStore.ConnectionLost();
        _time.Advance(TimeSpan.FromSeconds(10));
        await runtime.HandleAsync(Received(Snapshot(false)), CancellationToken.None);
        _store.Failure = null;
        _time.Advance(TimeSpan.FromSeconds(10));
        await runtime.HandleAsync(Received(Snapshot(false)), CancellationToken.None);
        await runtime.TickAsync(CancellationToken.None);
        Assert.Equal(3, _store.CountOf("insert-samples"));
        Assert.Equal(1, _store.CountOf("prepare"));

        _time.Advance(DatabaseGate.RetryAfter);
        await runtime.TickAsync(CancellationToken.None);
        Assert.Equal(2, _store.CountOf("prepare"));

        await runtime.HandleAsync(Received(Snapshot(false)), CancellationToken.None);
        Assert.Equal(4, _store.CountOf("insert-samples"));
    }

    [Fact]
    public async Task DrivesFromTheDatabaseReachSubscribersOnceItOpens()
    {
        _store.Failure = null;
        _store.StoredDrives.Add(new DriveState
        {
            Wwn = "naa.1",
            Port = null,
            Passed = true,
            ReallocatedSectorCount = 0,
            PendingSectorCount = 0,
            PowerOnHours = 100,
            SourcePath = "/dev/sdc",
            AsOf = "2026-10-01T12:00:00.000Z",
        });
        var runtime = Runtime();
        var subscriber = await Subscribe(runtime);
        Drain(subscriber);

        await runtime.TickAsync(CancellationToken.None);

        Assert.Contains(Drain(subscriber), json => json.Contains("""
            "type":"drives","drives":[{"wwn":"naa.1"
            """, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnUnreadableUpsIsRaisedOnlyOnceItHasStayedUnreadable()
    {
        var runtime = Runtime(nutHost: "10.0.0.4");
        var subscriber = await Subscribe(runtime);
        Drain(subscriber);

        await runtime.HandleAsync(new RuntimeInput.Ups(new UpsPoll.Failed("upsd: DATA-STALE")), CancellationToken.None);
        Assert.DoesNotContain(Drain(subscriber), json => json.Contains("ups-unreadable", StringComparison.Ordinal));

        _time.Advance(TimeSpan.FromSeconds(30));
        await runtime.TickAsync(CancellationToken.None);
        Assert.Contains(Drain(subscriber), json => json.Contains("ups-unreadable", StringComparison.Ordinal));
    }

    private static RuntimeInput.Ups UpsRead(params string[] status) => UpsRead(1, status);

    private static RuntimeInput.Ups UpsRead(int? monitors, params string[] status) =>
        new(new UpsPoll.Read(UpsConditionsTests.Ups(status, 40.0, 600.0, monitors: monitors).Reading!)); // Ups() always sets a reading.

    [Fact]
    public async Task ADeadUpsmonIsRaisedOnceItHasStayedGoneAndClearedWhenItIsBack()
    {
        var runtime = Runtime(nutHost: "10.0.0.4");
        var subscriber = await Subscribe(runtime);
        await runtime.HandleAsync(UpsRead(monitors: 1, "OL"), CancellationToken.None);
        Drain(subscriber);

        // Half a minute without one is a upsmon restarting, not yet a dead one.
        await runtime.HandleAsync(UpsRead(monitors: 0, "OL"), CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(30));
        await runtime.TickAsync(CancellationToken.None);
        Assert.DoesNotContain(Drain(subscriber), json => json.Contains("ups-unguarded", StringComparison.Ordinal));

        // No further poll needed: the tick notices that the gap has lasted.
        _time.Advance(TimeSpan.FromSeconds(30));
        await runtime.TickAsync(CancellationToken.None);
        Assert.Contains(Drain(subscriber), json => json.Contains("no upsmon is logged in", StringComparison.Ordinal));

        await runtime.HandleAsync(UpsRead(monitors: 1, "OL"), CancellationToken.None);
        Assert.Contains(Drain(subscriber), json => json.Contains("upsmon is watching the UPS again.", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("CHRG")]
    public async Task LosingTheUpsMidOutageNeverAnnouncesThatMainsIsBack(string? unusableStatus)
    {
        // A failed poll, or a reading that names no power source, says nothing about the mains.
        // Read as "not on battery", it once sent "Mains power is back" in the middle of an outage.
        var runtime = Runtime(nutHost: "10.0.0.4");
        var subscriber = await Subscribe(runtime);

        await runtime.HandleAsync(UpsRead("OB", "LB"), CancellationToken.None);
        Assert.Contains(Drain(subscriber), json => json.Contains("Mains power lost", StringComparison.Ordinal));

        var unusable = unusableStatus is null ? new RuntimeInput.Ups(new UpsPoll.Failed("upsd: DATA-STALE")) : UpsRead(unusableStatus);
        await runtime.HandleAsync(unusable, CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(30));
        await runtime.TickAsync(CancellationToken.None);

        var whileUnreadable = Drain(subscriber);
        Assert.Contains(whileUnreadable, json => json.Contains("ups-unreadable", StringComparison.Ordinal));
        Assert.DoesNotContain(whileUnreadable, json => json.Contains("Mains power is back", StringComparison.Ordinal));
        Assert.DoesNotContain(whileUnreadable, json => json.Contains("no longer low", StringComparison.Ordinal));

        // Only a reading that says so clears it.
        await runtime.HandleAsync(UpsRead("OL", "CHRG"), CancellationToken.None);
        var afterwards = Drain(subscriber);
        Assert.Contains(afterwards, json => json.Contains("Mains power is back", StringComparison.Ordinal));
        Assert.Contains(afterwards, json => json.Contains("The UPS is readable again.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADegradedPoolIsRaisedOnceHeldInTheVerdictRecordedAndClearedWhenItRecovers()
    {
        _store.Failure = null;
        var runtime = Runtime();
        await runtime.TickAsync(CancellationToken.None);
        var subscriber = await Subscribe(runtime);
        Drain(subscriber);

        var healthy = PoolSamples.Zfs(PoolSamples.Pool(), asOf: Rfc3339.From(_time.GetUtcNow()));
        var degraded = PoolSamples.Zfs(
            PoolSamples.Pool(state: "DEGRADED", vdevs: [PoolSamples.Disk("a", "1", "FAULTED", read: 4), PoolSamples.Disk("b", "2")]),
            asOf: Rfc3339.From(_time.GetUtcNow().AddSeconds(60)));

        await runtime.HandleAsync(Received(Snapshot(false) with { Zfs = healthy }), CancellationToken.None);
        await runtime.HandleAsync(Received(Snapshot(false) with { Zfs = degraded }), CancellationToken.None);

        // The same poll repeated in every snapshot is not judged again.
        await runtime.HandleAsync(Received(Snapshot(false) with { Zfs = degraded }), CancellationToken.None);
        await runtime.TickAsync(CancellationToken.None);

        var messages = Drain(subscriber);
        var raised = messages.Where(json => json.Contains("\"type\":\"event\"", StringComparison.Ordinal)).ToList();
        Assert.Contains(raised, json => json.Contains("zfs-pool:void", StringComparison.Ordinal));
        Assert.Contains(raised, json => json.Contains("zfs-vdev:void:1", StringComparison.Ordinal));
        Assert.Contains(raised, json => json.Contains("zfs-vdev-errors:void:1", StringComparison.Ordinal));
        Assert.Equal(3, raised.Count);

        var verdict = messages.Last(json => json.Contains("\"type\":\"alerts\"", StringComparison.Ordinal));
        Assert.Contains("zfs-pool:void", verdict, StringComparison.Ordinal);
        Assert.Contains(_store.SavedPools, update => update.VdevErrors.Count == 1);

        await runtime.HandleAsync(
            Received(Snapshot(false) with { Zfs = healthy with { AsOf = Rfc3339.From(_time.GetUtcNow().AddSeconds(120)) } }),
            CancellationToken.None);
        var after = Drain(subscriber);
        Assert.Contains(after, json => json.Contains("Pool void is ONLINE again.", StringComparison.Ordinal));
        Assert.Contains("\"alerts\":[]", after.Last(json => json.Contains("\"type\":\"alerts\"", StringComparison.Ordinal)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedZpoolPollDoesNotDeclareADegradedPoolHealthy()
    {
        var runtime = Runtime();
        var degraded = PoolSamples.Zfs(PoolSamples.Pool(state: "DEGRADED"), asOf: Rfc3339.From(_time.GetUtcNow()));
        await runtime.HandleAsync(Received(Snapshot(false) with { Zfs = degraded }), CancellationToken.None);
        var subscriber = await Subscribe(runtime);
        Drain(subscriber);

        await runtime.HandleAsync(
            Received(Snapshot(false) with { Zfs = PoolSamples.Unreadable(Rfc3339.From(_time.GetUtcNow().AddSeconds(60))) }),
            CancellationToken.None);

        var messages = Drain(subscriber);
        Assert.DoesNotContain(messages, json => json.Contains("ONLINE again", StringComparison.Ordinal));
        var verdict = messages.Last(json => json.Contains("\"type\":\"alerts\"", StringComparison.Ordinal));
        Assert.Contains("zfs-pool:void", verdict, StringComparison.Ordinal);
        Assert.Contains("zfs-unreadable", verdict, StringComparison.Ordinal);
    }
}
