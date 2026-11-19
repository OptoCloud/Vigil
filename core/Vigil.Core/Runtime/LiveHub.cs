using System.Threading.Channels;

namespace Vigil.Core.Runtime;

/// <summary>
/// Hands out live subscriptions: caps how many are open, and attaches each one to the runtime.
/// </summary>
/// <remarks>
/// The cap is decided here rather than in the runtime because a refusal has to be an HTTP 503
/// before the response starts, and the runtime's loop cannot answer synchronously.
/// </remarks>
public sealed class LiveHub(ChannelWriter<RuntimeInput> inputs)
{
    /// <summary>
    /// How many browser streams may be open at once (docs/SECURITY.md §4.2). One tab is one
    /// stream and a closed tab frees its slot at once. The page is public (ADR-023), so this
    /// is sized for strangers looking as well as for its own users; what a stream costs is a
    /// connection and a queue of references to strings every stream shares.
    /// </summary>
    public const int MaxSubscribers = 256;

    /// <summary>
    /// How many of those one client may hold, so that filling the page takes many addresses
    /// rather than one loop. A browser opens at most six connections to a host; eight leaves
    /// room for a second device behind the same address.
    /// </summary>
    public const int MaxSubscribersPerClient = 8;

    /// <summary>
    /// Messages a subscriber may fall behind by before it is dropped. The stream carries events,
    /// which the next message does not supersede, so the backlog is deep.
    /// </summary>
    private const int SubscriberBacklog = 256;

    private int _open;

    /// <summary>
    /// Subscriptions queued for the runtime and not yet taken by it. Capped like the open ones:
    /// a closed stream frees its slot at once, so without this a client connecting and
    /// disconnecting in a loop would queue one of these each time for as long as the runtime
    /// is busy, and its queue is shared with the pollers.
    /// </summary>
    private int _queued;

    /// <summary>Open streams per client. Only clients with one open have an entry.</summary>
    private readonly Dictionary<string, int> _perClient = new(StringComparer.Ordinal);

    /// <summary>
    /// A new subscription, or null when <see cref="MaxSubscribers"/> are already open or still
    /// waiting for the runtime, when this client holds <see cref="MaxSubscribersPerClient"/>,
    /// or when the runtime has stopped or its queue is full. The caller disposes it when its stream ends.
    /// </summary>
    /// <param name="client">Who is asking, as <c>Admission.ClientKey</c> names them.</param>
    public LiveSubscription? TrySubscribe(string client)
    {
        if (!TryCount(client))
        {
            return null;
        }

        if (Interlocked.Increment(ref _open) > MaxSubscribers)
        {
            Interlocked.Decrement(ref _open);
            Uncount(client);
            return null;
        }

        if (Interlocked.Increment(ref _queued) > MaxSubscribers)
        {
            Interlocked.Decrement(ref _queued);
            Interlocked.Decrement(ref _open);
            Uncount(client);
            return null;
        }

        var subscription = new LiveSubscription(SubscriberBacklog, () =>
        {
            Interlocked.Decrement(ref _open);
            Uncount(client);
        });
        if (!inputs.TryWrite(new RuntimeInput.Subscribe(subscription.Writer, () => Interlocked.Decrement(ref _queued))))
        {
            Interlocked.Decrement(ref _queued);
            subscription.Dispose();
            return null;
        }

        return subscription;
    }

    private bool TryCount(string client)
    {
        lock (_perClient)
        {
            var held = _perClient.GetValueOrDefault(client);
            if (held >= MaxSubscribersPerClient)
            {
                return false;
            }

            _perClient[client] = held + 1;
            return true;
        }
    }

    private void Uncount(string client)
    {
        lock (_perClient)
        {
            // Removed at zero, so the table is never larger than the streams that are open.
            if (_perClient.GetValueOrDefault(client) is > 1 and var held)
            {
                _perClient[client] = held - 1;
            }
            else
            {
                _perClient.Remove(client);
            }
        }
    }
}

/// <summary>One live stream's queue. Disposing it frees its slot and tells the runtime to drop it.</summary>
public sealed class LiveSubscription : IDisposable
{
    private readonly Channel<string> _messages;
    private readonly Action _release;
    private int _disposed;

    internal LiveSubscription(int backlog, Action release)
    {
        // Wait, not DropOldest: a full queue makes the runtime's TryWrite fail, which is how it
        // knows to drop a subscriber that cannot keep up. Silently losing an event would be worse.
        _messages = Channel.CreateBounded<string>(
            new BoundedChannelOptions(backlog) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        _release = release;
    }

    public ChannelReader<string> Reader => _messages.Reader;

    internal ChannelWriter<string> Writer => _messages.Writer;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            // Completing the writer makes the runtime's next write to it fail, so it is dropped.
            _messages.Writer.TryComplete();
            _release();
        }
    }
}
