using System.Net.Http.Headers;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Vigil.Core.Alerting;
using Vigil.Core.Protocol;

namespace Vigil.Core.Notifications;

/// <summary>
/// Text messages through a self-hosted SMS gateway (ADR-027). vigil defines the
/// contract and the gateway implements it: a JSON POST with a bearer token, 2xx for accepted.
/// vigil-core never drives the modem itself (invariant 1), and knows no phone number: the
/// gateway holds them, so none is in vigil's config or its log.
/// </summary>
/// <remarks>
/// Built as <see cref="NtfyClient"/> is: fire and forget, a hard time limit, the reply's body
/// never read, a failure logged and never thrown. Neither the URL nor the token is ever logged.
/// </remarks>
public sealed class SmsClient(HttpClient http, Uri url, string token, ILogger<SmsClient> logger) : INotifier
{
    /// <summary>The gateway is on the LAN; a text later than this is noise, as for ntfy.</summary>
    public static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(10);

    /// <summary>A gateway's reply needs a handful of headers; SocketsHttpHandler's unit is KiB.</summary>
    public const int MaximumHeaderKilobytes = 16;

    public string Name => "sms";

    /// <summary>Warnings and worse, with their all-clears, as decided on 2026-11-11: the same as ntfy.</summary>
    public Severity MinimumSeverity => Severity.Warning;

    /// <summary>Starts sending and returns at once; a failure is logged, never thrown.</summary>
    public void Send(Notification notification) => _ = SendAsync(notification);

    /// <summary>What the text says: who sent it and how bad it is, then the event's own words.</summary>
    internal static string TextOf(Notification notification) =>
        (notification.IsAllClear ? "vigil resolved: "
            : notification.Event.Severity == Severity.Critical ? "vigil CRITICAL: "
            : "vigil warning: ") + notification.Event.Message;

    internal async Task SendAsync(Notification notification)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                // Source-generated, as everything serialized is under NativeAOT (ADR-016).
                Content = System.Net.Http.Json.JsonContent.Create(new SmsMessage(TextOf(notification)), SmsJsonContext.Default.SmsMessage),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var timeout = new CancellationTokenSource(SendTimeout);

            // ResponseHeadersRead: only the status is used, so the body is never read.
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("could not send a text: the SMS gateway answered HTTP {Status}", (int)response.StatusCode);
            }
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException)
        {
            // The exception's message, never the request: the URL and the token stay out of the log.
            logger.LogWarning("could not send a text: {Error}", error is OperationCanceledException ? "the SMS gateway did not answer in time" : error.Message);
        }
    }
}

/// <summary>The gateway's request body: <c>{ "message": "…" }</c>.</summary>
public sealed record SmsMessage(string Message);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SmsMessage))]
internal sealed partial class SmsJsonContext : JsonSerializerContext;
