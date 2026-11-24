using Microsoft.Extensions.Hosting;
using Vigil.Core.Configuration;
using Vigil.Core.Runtime;

namespace Vigil.Core.Clients;

/// <summary>
/// Tells something outside the host that vigil-core is alive: a GET to <c>HEARTBEAT_URL</c>
/// every <c>HEARTBEAT_INTERVAL_SECONDS</c>, for a service that raises the alarm when the
/// requests stop. Every alert vigil sends needs vigil-core running to send it; a dead
/// vigil-core, a dead guest or a dead host is silent, and this is what makes silence audible.
/// </summary>
/// <remarks>
/// It is sent only while the runtime's loop is turning, so a process that is up but stuck
/// stops it too. The URL is the check's only secret and is never logged (SECURITY.md §7).
/// A failure is logged once, and once more when it is over: at one a minute, a line for
/// each would bury the log during exactly the outage someone is reading it for.
/// </remarks>
public sealed class Heartbeat(HttpClient http, Uri url, TimeSpan interval, RuntimePulse pulse, ILogger<Heartbeat> logger, TimeProvider time)
    : BackgroundService
{
    /// <summary>A heartbeat service answers at once; one that has not by now is counted as a miss.</summary>
    public static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Only the status is read; SocketsHttpHandler's unit is KiB.</summary>
    public const int MaximumHeaderKilobytes = 16;

    private string? _failing;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await BeatOnceAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(interval, time, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The host is stopping.
        }
    }

    /// <summary>One beat, or none while the loop is not turning. True when it was sent and accepted.</summary>
    internal async Task<bool> BeatOnceAsync(CancellationToken cancellationToken)
    {
        if (!pulse.IsTurning(Limits.LoopStalledAfter))
        {
            Report("the runtime's loop is not turning, so no heartbeat is sent");
            return false;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SendTimeout);
        try
        {
            // ResponseHeadersRead: only the status is used, so the body is never read.
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            Report(response.IsSuccessStatusCode ? null : $"the heartbeat was answered with HTTP {(int)response.StatusCode}");
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Report("the heartbeat was not answered in time");
            return false;
        }
        catch (HttpRequestException error)
        {
            // The exception's message, never the request: the URL stays out of the log.
            Report($"the heartbeat could not be sent: {error.Message}");
            return false;
        }
    }

    /// <summary>Logs a change of state: the first failure of a run, a different failure, and the recovery.</summary>
    private void Report(string? failure)
    {
        if (failure == _failing)
        {
            return;
        }

        if (failure is null)
        {
            logger.LogInformation("the heartbeat is being accepted again");
        }
        else
        {
            logger.LogWarning("{Failure}", failure);
        }

        _failing = failure;
    }
}
