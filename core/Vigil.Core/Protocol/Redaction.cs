namespace Vigil.Core.Protocol;

/// <summary>
/// What stands on the public page (ADR-023) for a value, or the part of one, that is not
/// published: a credential, a serial number, an address.
/// </summary>
/// <remarks>
/// One marker for all of them, and a loud one. A reader has to be able to tell "withheld" from
/// "the device reported nothing" and from a value that merely looks odd: an ellipsis in front of
/// four characters read as a short serial, not as a cut one. What gets redacted is decided where
/// each value enters the process (<see cref="SerialNumbers"/>, <see cref="Clients.NutProtocol"/>);
/// this only says how it reads. The page draws it as a masked chip (<c>web/src/lib/redaction.ts</c>,
/// which core/contract.json pins to this value), so it is loud only where loud helps: in the
/// database, a log, a notification.
/// </remarks>
public static class Redaction
{
    public const string Marker = "[REDACTED]";
}
