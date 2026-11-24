using System.Globalization;
using System.Net;
using System.Net.Mail;
using System.Net.Sockets;
using System.Security.Authentication;
using Vigil.Core.Protocol;

namespace Vigil.Core.Notifications;

/// <summary>Where mail goes and who it is from. All of it private: never logged, never served.</summary>
public sealed record EmailSettings
{
    public required string Host { get; init; }
    public required int Port { get; init; }

    /// <summary>Upgrade the connection with STARTTLS before anything else is said.</summary>
    public required bool StartTls { get; init; }

    /// <summary>Null for a relay that asks for no login.</summary>
    public NetworkCredential? Credentials { get; init; }

    public required MailAddress From { get; init; }
    public required IReadOnlyList<MailAddress> To { get; init; }
}

/// <summary>
/// Email over SMTP, the second way vigil reaches a person (ADR-028). The platform's own
/// <see cref="SmtpClient"/> (ADR-015): one message to a server the owner named is all of SMTP
/// vigil needs, and it brings no dependency.
/// </summary>
/// <remarks>
/// Like <see cref="SmsClient"/>: a hard time limit, a failure reported and never thrown, and
/// nothing private in the report. A mail server's reply can quote the recipient back, so only
/// its status code is repeated, never its words.
/// </remarks>
public sealed class EmailClient(EmailSettings settings) : IDelivery
{
    /// <summary>
    /// Connect, STARTTLS, log in and hand over one short message. A server on the internet
    /// can take seconds over each; one that takes longer than this in all is not working.
    /// </summary>
    public static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The longest subject sent. Mail clients cut a subject near here anyway; the body has the rest.</summary>
    public const int MaximumSubjectLength = 150;

    public string Name => "email";

    /// <summary>Warnings and worse, with their all-clears: the same as a text.</summary>
    public Severity MinimumSeverity => Severity.Warning;

    /// <summary>
    /// The message for a notification. Control characters are replaced in the subject: an
    /// event's words can carry a pool's or a device's name, and a line break in a header is
    /// how a header is forged.
    /// </summary>
    internal MailMessage MessageFor(Notification notification, DateTimeOffset now)
    {
        var headline = string.Concat(notification.Headline.Select(character => char.IsControl(character) ? ' ' : character));
        var message = new MailMessage
        {
            From = settings.From,
            Subject = headline.Length > MaximumSubjectLength ? headline[..(MaximumSubjectLength - 1)] + "…" : headline,
            Body = string.Create(
                CultureInfo.InvariantCulture,
                $"{notification.Headline}\n\nKind: {notification.Event.Kind}\nSent: {now.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC\n"),
        };
        foreach (var recipient in settings.To)
        {
            message.To.Add(recipient);
        }

        return message;
    }

    public async Task<string?> TryDeliverAsync(Notification notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SendTimeout);
        try
        {
            // One client per message: a handful are sent a month, and a new one resolves the
            // server's name again, so a changed address is picked up.
            using var smtp = new SmtpClient(settings.Host, settings.Port)
            {
                EnableSsl = settings.StartTls,
                DeliveryMethod = SmtpDeliveryMethod.Network,
                UseDefaultCredentials = false,
                Credentials = settings.Credentials,
            };
            using var message = MessageFor(notification, DateTimeOffset.UtcNow);
            await smtp.SendMailAsync(message, timeout.Token).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return "the mail server did not finish in time";
        }
        catch (Exception error) when (error is SmtpException or IOException or SocketException or AuthenticationException)
        {
            // Never error.Message: it carries the server's own reply, which can name a mailbox.
            return error.GetBaseException() switch
            {
                AuthenticationException => "the mail server's TLS could not be trusted",
                SocketException or IOException => "the mail server could not be reached",
                SmtpException refused => $"the mail server refused the message ({refused.StatusCode})",
                _ => "the mail could not be sent",
            };
        }
    }
}
