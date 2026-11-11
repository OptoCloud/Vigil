using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Vigil.Core.Alerting;
using Vigil.Core.Clients;
using Vigil.Core.Configuration;
using Vigil.Core.Protocol;
using Vigil.Core.Runtime;
using Xunit;

namespace Vigil.Core.Tests;

/// <summary>
/// The runtime's verdict (ADR-019): what holds it, what clears it, and what must not. Split from
/// VigilRuntimeTests for the size ceiling (docs/STYLE.md §2.1); the fixtures are shared.
/// </summary>
public sealed partial class VigilRuntimeTests
{
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

    [Fact]
    public async Task AFailedDriveHoldsTheVerdictAndIsRaisedAgainFromTheDatabaseAfterARestart()
    {
        _store.Failure = null;
        var failing = new DriveHealth
        {
            DeviceName = "naa.1",
            Passed = false,
            ReallocatedSectorCount = 0,
            PendingSectorCount = 0,
            PowerOnHours = 100,
            SourcePath = "/dev/sda",
            IsAvailable = true,
            AsOf = "2026-10-01T12:00:00.000Z",
        };
        var runtime = Runtime();
        var subscriber = await Subscribe(runtime);
        Drain(subscriber);

        await runtime.HandleAsync(Received(Snapshot(false) with { DriveHealth = [failing] }), CancellationToken.None);
        // Asleep on the next poll: the failure is still the drive's last good word.
        await runtime.HandleAsync(
            Received(Snapshot(false) with { DriveHealth = [failing with { IsAvailable = false, Passed = null, AsOf = "2026-10-01T12:30:00.000Z" }] }),
            CancellationToken.None);

        var messages = Drain(subscriber);
        Assert.Single(messages, json => json.Contains("\"type\":\"event\"", StringComparison.Ordinal) && json.Contains("drive-failed:naa.1", StringComparison.Ordinal));
        Assert.Contains("drive-failed:naa.1", messages.Last(json => json.Contains("\"type\":\"alerts\"", StringComparison.Ordinal)), StringComparison.Ordinal);

        // A restart: the database's last good state raises it again before any poll arrives.
        _store.StoredDrives.Add(DriveHealthDiff.Diff(null, failing).State!);
        var restarted = Runtime();
        await restarted.TickAsync(CancellationToken.None);
        var after = Drain(await Subscribe(restarted));
        Assert.Contains("drive-failed:naa.1", after.Single(json => json.Contains("\"type\":\"alerts\"", StringComparison.Ordinal)), StringComparison.Ordinal);
    }
}
