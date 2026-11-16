using System.Net.ServerSentEvents;
using System.Threading.Channels;
using Vigil.Core.Api;
using Vigil.Core.Runtime;
using Xunit;

namespace Vigil.Core.Tests;

public sealed class LiveHubTests
{
    private readonly Channel<RuntimeInput> _inputs = Channel.CreateUnbounded<RuntimeInput>();

    private ChannelWriter<string> SubscriberAskedFor()
    {
        Assert.True(_inputs.Reader.TryRead(out var input));
        return Assert.IsType<RuntimeInput.Subscribe>(input).Subscriber;
    }

    private static async Task<List<SseItem<string>>> Collect(IAsyncEnumerable<SseItem<string>> stream, int atMost)
    {
        var items = new List<SseItem<string>>();
        await foreach (var item in stream)
        {
            items.Add(item);
            if (items.Count == atMost)
            {
                break;
            }
        }

        return items;
    }

    private int _clients;

    /// <summary>A client no other call has used, so only the cap under test is in play.</summary>
    private string AnotherClient() => $"client-{_clients++}";

    [Fact]
    public void OneClientCannotHoldMoreThanItsShareAndGetsASlotBackWhenAStreamEnds()
    {
        var hub = new LiveHub(_inputs.Writer);
        var held = Enumerable.Range(0, LiveHub.MaxSubscribersPerClient).Select(_ => hub.TrySubscribe("203.0.113.7")).ToList();
        Assert.All(held, Assert.NotNull);

        Assert.Null(hub.TrySubscribe("203.0.113.7"));

        // Everyone else is unaffected, which is the point of counting per client.
        using var other = hub.TrySubscribe("203.0.113.8");
        Assert.NotNull(other);

        held[0]!.Dispose();
        Assert.NotNull(hub.TrySubscribe("203.0.113.7"));
        Assert.Null(hub.TrySubscribe("203.0.113.7"));
    }

    /// <summary>The runtime's side: takes everything queued for it, as its loop does.</summary>
    private int RuntimeTakesWhatIsQueued()
    {
        var taken = 0;
        while (_inputs.Reader.TryRead(out var input))
        {
            Assert.IsType<RuntimeInput.Subscribe>(input).Dequeued?.Invoke();
            taken++;
        }

        return taken;
    }

    [Fact]
    public void AStreamOpenedAndClosedInALoopCannotGrowTheRuntimesQueue()
    {
        // The runtime is busy (a slow database) and takes nothing. Each stream closes at once,
        // so the cap on open streams never bites: the cap on queued ones has to.
        var hub = new LiveHub(_inputs.Writer);
        for (var attempt = 0; attempt < LiveHub.MaxSubscribers * 10; attempt++)
        {
            hub.TrySubscribe(AnotherClient())?.Dispose();
        }

        Assert.Null(hub.TrySubscribe(AnotherClient()));
        Assert.Equal(LiveHub.MaxSubscribers, RuntimeTakesWhatIsQueued());

        // Once the runtime has caught up, streams attach again.
        using var subscription = hub.TrySubscribe(AnotherClient());
        Assert.NotNull(subscription);
    }

    [Fact]
    public void RefusesPastTheCapAndFreesASlotWhenAStreamEnds()
    {
        var hub = new LiveHub(_inputs.Writer);
        var open = Enumerable.Range(0, LiveHub.MaxSubscribers).Select(_ => hub.TrySubscribe(AnotherClient())).ToList();
        Assert.All(open, Assert.NotNull);
        RuntimeTakesWhatIsQueued();

        Assert.Null(hub.TrySubscribe(AnotherClient()));

        open[0]!.Dispose();
        open[0]!.Dispose();
        Assert.NotNull(hub.TrySubscribe(AnotherClient()));
        Assert.Null(hub.TrySubscribe(AnotherClient()));
    }

    [Fact]
    public void RefusesOnceTheRuntimeHasStoppedWithoutLeakingTheSlot()
    {
        var hub = new LiveHub(_inputs.Writer);
        _inputs.Writer.Complete();

        for (var attempt = 0; attempt <= LiveHub.MaxSubscribers; attempt++)
        {
            Assert.Null(hub.TrySubscribe(AnotherClient()));
        }
    }

    [Fact]
    public async Task RelaysWhatTheRuntimeQueuesInOrderAndEndsWhenTheRuntimeDropsTheStream()
    {
        using var subscription = new LiveHub(_inputs.Writer).TrySubscribe(AnotherClient())!;
        var runtimeSide = SubscriberAskedFor();
        runtimeSide.TryWrite("""{"type":"daemon","connected":true}""");
        runtimeSide.TryWrite("""{"type":"daemon","connected":false}""");
        runtimeSide.TryComplete();

        var items = await Collect(LiveEndpoint.Relay(subscription, TimeSpan.FromMinutes(1), CancellationToken.None), 10);

        Assert.Equal(["""{"type":"daemon","connected":true}""", """{"type":"daemon","connected":false}"""], items.Select(item => item.Data));

        // Unnamed, so EventSource's onmessage gets them: the default type, which the formatter
        // leaves off the wire (ApiTests checks the bytes).
        Assert.All(items, item => Assert.Equal(SseParser.EventTypeDefault, item.EventType));
    }

    [Fact]
    public async Task SendsAKeepaliveWhenTheStreamIsQuiet()
    {
        using var subscription = new LiveHub(_inputs.Writer).TrySubscribe(AnotherClient())!;

        var items = await Collect(LiveEndpoint.Relay(subscription, TimeSpan.FromMilliseconds(20), CancellationToken.None), 2);

        Assert.All(items, item => Assert.Equal("keepalive", item.EventType));
    }

    [Fact]
    public async Task ABrowserThatLeavesFreesItsSlotAndTheRuntimeStopsWritingToIt()
    {
        var hub = new LiveHub(_inputs.Writer);
        var others = Enumerable.Range(0, LiveHub.MaxSubscribers - 1).Select(_ => hub.TrySubscribe(AnotherClient())).ToList();
        var leaving = hub.TrySubscribe(AnotherClient())!;
        RuntimeTakesWhatIsQueued();
        Assert.Null(hub.TrySubscribe(AnotherClient()));

        using var browser = new CancellationTokenSource();
        var relay = Collect(LiveEndpoint.Relay(leaving, TimeSpan.FromMinutes(1), browser.Token), 10);
        await browser.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => relay);

        Assert.NotNull(hub.TrySubscribe(AnotherClient()));
        Assert.True(leaving.Reader.Completion.IsCompleted);
    }
}
