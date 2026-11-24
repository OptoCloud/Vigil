using System.Net.Mail;

namespace Vigil.Core.Configuration;

/// <summary><c>EMAIL_TO</c>: one address or several, separated by commas.</summary>
public static class MailAddresses
{
    /// <summary>
    /// The most recipients accepted. A household's worth; a longer list is a mailing list,
    /// which is one address.
    /// </summary>
    public const int MaximumRecipients = 10;

    /// <summary>
    /// Parses <paramref name="text"/>. False when it is empty, names too many, or holds
    /// something that is not an address. Which entry failed is not reported: an address is
    /// private, and a problem is printed to the log.
    /// </summary>
    public static bool TryParse(string? text, out IReadOnlyList<MailAddress> addresses)
    {
        var parsed = new List<MailAddress>();
        addresses = parsed;

        foreach (var entry in (text ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!MailAddress.TryCreate(entry, out var address))
            {
                return false;
            }

            parsed.Add(address);
        }

        return parsed.Count is > 0 and <= MaximumRecipients;
    }

    /// <summary>The addresses from an already-validated option.</summary>
    public static IReadOnlyList<MailAddress> Parse(string? text) =>
        TryParse(text, out var addresses) ? addresses : throw new InvalidOperationException("EMAIL_TO was not validated before use.");
}
