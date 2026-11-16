using System.Threading.Channels;
using Vigil.Core.Clients;

namespace Vigil.Core.Runtime;

/// <summary>Everything that reaches the runtime, on its one input channel, in the order it happened.</summary>
public abstract record RuntimeInput
{
    public sealed record Daemon(DaemonEvent Event) : RuntimeInput;

    public sealed record Ups(UpsPoll Poll) : RuntimeInput;

    /// <summary>
    /// A new live subscriber: gets the current state at once, then every change as one JSON
    /// message each. The runtime completes the writer when it drops the subscriber.
    /// </summary>
    /// <param name="Subscriber">Where the runtime writes this stream's messages.</param>
    /// <param name="Dequeued">
    /// Called by the runtime when it takes this off its queue, which is what lets
    /// <see cref="LiveHub"/> bound how many of these are waiting there.
    /// </param>
    public sealed record Subscribe(ChannelWriter<string> Subscriber, Action? Dequeued = null) : RuntimeInput;
}
