using Vigil.Core.Protocol;
using Xunit;

namespace Vigil.Core.Tests;

/// <summary>The host's load through the runtime: what the page is sent and what history gets (ADR-025).</summary>
public sealed partial class VigilRuntimeTests
{
    private static Snapshot WithHost(int seconds, ulong readBytes) => Snapshot(false) with
    {
        Host = new HostStatus
        {
            AsOf = Rfc3339.From(new DateTimeOffset(2026, 10, 1, 12, 0, seconds, TimeSpan.Zero)),
            UptimeSeconds = 1_000 + seconds,
            Memory = null,
            Cpu = null,
            Load = null,
            Pressure = null,
            Disks = [new DiskCounters { DeviceName = "naa.1", Port = null, ReadBytes = readBytes, WrittenBytes = 0, BusyMillis = 0 }],
            Interfaces = [],
        },
    };

    private static List<string> HostMessages(IEnumerable<string> messages) =>
        [.. messages.Where(json => json.StartsWith("""{"type":"host" """.TrimEnd(), StringComparison.Ordinal))];

    [Fact]
    public async Task EachNewHostReadingIsSentOnceAndHistoryGetsTheAverageSinceItsLastRow()
    {
        _store.Failure = null;
        var runtime = Runtime();
        await runtime.TickAsync(CancellationToken.None);
        var subscriber = await Subscribe(runtime);
        Drain(subscriber);

        // vigild repeats its host reading in every snapshot until the next one: only a new one is news.
        await runtime.HandleAsync(Received(WithHost(0, 0)), CancellationToken.None);
        await runtime.HandleAsync(Received(WithHost(0, 0)), CancellationToken.None);
        await runtime.HandleAsync(Received(WithHost(2, 20_000_000)), CancellationToken.None);
        var sent = HostMessages(Drain(subscriber));
        Assert.Equal(2, sent.Count);
        Assert.Contains("\"readBytesPerSecond\":10000000", sent[1], StringComparison.Ordinal);

        // The first row has nothing to average against; the next covers everything since it.
        Assert.Single(_store.HostSamples);
        _time.Advance(TimeSpan.FromSeconds(10));
        await runtime.HandleAsync(Received(WithHost(10, 30_000_000)), CancellationToken.None);
        Assert.Equal(2, _store.HostSamples.Count);
        Assert.Equal(3_000_000, Assert.Single(_store.HostSamples[1].Disks).ReadBytesPerSecond);

        // A late subscriber is sent the latest load straight away.
        var late = HostMessages(Drain(await Subscribe(runtime)));
        Assert.Contains("\"readBytesPerSecond\":1250000", Assert.Single(late), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutADatabaseTheLoadStillReachesThePage()
    {
        var runtime = Runtime();
        var subscriber = await Subscribe(runtime);
        Drain(subscriber);

        await runtime.HandleAsync(Received(WithHost(0, 0)), CancellationToken.None);
        await runtime.HandleAsync(Received(WithHost(2, 2_000)), CancellationToken.None);

        Assert.Equal(2, HostMessages(Drain(subscriber)).Count);
        Assert.Empty(_store.HostSamples);
    }
}
