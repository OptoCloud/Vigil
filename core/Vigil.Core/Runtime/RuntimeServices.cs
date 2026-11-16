using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Vigil.Core.Clients;

namespace Vigil.Core.Runtime;

// The three long-running loops, as hosted services. Each poller only forwards what it reads into
// the runtime's input channel; everything that decides anything happens on the runtime's loop.

/// <summary>Runs the runtime. When it stops, the input channel is closed so no new stream attaches.</summary>
public sealed class RuntimeService(VigilRuntime runtime, Channel<RuntimeInput> inputs) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await runtime.RunAsync(inputs.Reader, stoppingToken).ConfigureAwait(false);
        }
        finally
        {
            inputs.Writer.TryComplete();
        }
    }
}

/// <summary>vigild's stream, reconnecting forever (the client's own loop).</summary>
public sealed class DaemonFeed(VigildClient client, Channel<RuntimeInput> inputs) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var daemonEvent in client.StreamAsync(stoppingToken).ConfigureAwait(false))
        {
            if (!await Feed.ForwardAsync(inputs.Writer, new RuntimeInput.Daemon(daemonEvent), stoppingToken).ConfigureAwait(false))
            {
                return;
            }
        }
    }
}

/// <summary>upsd's readings. Registered only when NUT_HOST is set.</summary>
public sealed class UpsFeed(NutClient client, Channel<RuntimeInput> inputs) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var poll in client.PollAsync(stoppingToken).ConfigureAwait(false))
        {
            if (!await Feed.ForwardAsync(inputs.Writer, new RuntimeInput.Ups(poll), stoppingToken).ConfigureAwait(false))
            {
                return;
            }
        }
    }
}

/// <summary>How a poller hands the runtime what it read.</summary>
internal static class Feed
{
    /// <summary>
    /// Queues <paramref name="input"/>, waiting while the runtime's queue is full: a connection
    /// event must not be dropped, and a poller that waits stops reading its socket, which is
    /// the backpressure a bounded queue is for. False once the runtime has stopped or the host
    /// is stopping, which ends the poller without failing it.
    /// </summary>
    public static async ValueTask<bool> ForwardAsync(ChannelWriter<RuntimeInput> inputs, RuntimeInput input, CancellationToken stoppingToken)
    {
        try
        {
            await inputs.WriteAsync(input, stoppingToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception error) when (error is ChannelClosedException or OperationCanceledException)
        {
            return false;
        }
    }
}
