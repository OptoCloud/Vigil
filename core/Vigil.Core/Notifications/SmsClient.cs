using System.Net.Http.Headers;
using System.Text.Json.Serialization;
using Vigil.Core.Protocol;

namespace Vigil.Core.Notifications;

/// <summary>
/// Text messages through a self-hosted SMS gateway (ADR-027). vigil defines the
/// contract and the gateway implements it: a JSON POST with a bearer token, 2xx for accepted.
/// vigil-core never drives the modem itself (invariant 1), and knows no phone number: the
/// gateway holds them, so none is in vigil's config or its log.
/// </summary>
/// <remarks>
/// A hard time limit, the reply's body never read, a failure reported and never thrown.
/// Neither the URL nor the token is in what it reports.
/// </remarks>
public sealed class SmsClient(HttpClient http, Uri url, string token) : IDelivery
{
    /// <summary>The gateway is on the LAN; one that has not answered by now will not.</summary>
    public static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(10);

    /// <summary>A gateway's reply needs a handful of headers; SocketsHttpHandler's unit is KiB.</summary>
    public const int MaximumHeaderKilobytes = 16;

    public string Name => "sms";

    /// <summary>Warnings and worse, with their all-clears, as decided on 2026-11-11.</summary>
    public Severity MinimumSeverity => Severity.Warning;

    public async Task<string?> TryDeliverAsync(Notification notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SendTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                // Source-generated, as everything serialized is under NativeAOT (ADR-016).
                Content = System.Net.Http.Json.JsonContent.Create(new SmsMessage(notification.Headline), SmsJsonContext.Default.SmsMessage),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            // ResponseHeadersRead: only the status is used, so the body is never read.
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? null : $"the SMS gateway answered HTTP {(int)response.StatusCode}";
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return "the SMS gateway did not answer in time";
        }
        catch (HttpRequestException error)
        {
            // The exception's message, never the request: the URL and the token stay out of the log.
            return error.Message;
        }
    }
}

/// <summary>The gateway's request body: <c>{ "message": "…" }</c>.</summary>
public sealed record SmsMessage(string Message);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SmsMessage))]
internal sealed partial class SmsJsonContext : JsonSerializerContext;
