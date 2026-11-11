using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using Vigil.Core.Alerting;
using Vigil.Core.Protocol;

namespace Vigil.Core.Notifications;

/// <summary>
/// Push notifications through ntfy (https://ntfy.sh or self-hosted). Optional, and never allowed
/// to break or stall anything: each send runs off the runtime's loop with a hard time limit.
/// Ported from the Rust core's ntfy.rs, which had to run curl because HTTPS in Rust needed a TLS stack
/// with C in it. .NET has HTTPS built in, so this is an HttpClient (ADR-003 binds vigild only).
/// </summary>
public sealed class NtfyClient(HttpClient http, Uri url, ILogger<NtfyClient> logger) : INotifier
{
    /// <summary>The same bound curl's --max-time 10 gave: a notification later than this is noise.</summary>
    public static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(10);

    public string Name => "ntfy";

    /// <summary>Warnings and worse: ntfy is a phone notification, cheap to send and to dismiss.</summary>
    public Severity MinimumSeverity => Severity.Warning;

    /// <summary>Starts sending and returns at once; a failure is logged, never thrown.</summary>
    public void Send(Notification notification) => _ = SendAsync(notification);

    internal async Task SendAsync(Notification notification)
    {
        var newEvent = notification.Event;
        var (title, priority, tags) = notification.IsAllClear
            ? ("vigil: resolved", "default", "white_check_mark")
            : newEvent.Severity == Severity.Critical
                ? ("vigil: CRITICAL", "urgent", "rotating_light")
                : ("vigil: warning", "default", "warning");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(newEvent.Message, Encoding.UTF8, new MediaTypeHeaderValue("text/plain")),
            };
            request.Headers.Add("Title", title);
            request.Headers.Add("Priority", priority);
            request.Headers.Add("Tags", tags);

            using var timeout = new CancellationTokenSource(SendTimeout);
            using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("could not send a notification: ntfy answered HTTP {Status}", (int)response.StatusCode);
            }
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException)
        {
            // The URL is not logged: a private topic's name is its only secret.
            logger.LogWarning("could not send a notification: {Error}", error.Message);
        }
    }
}
