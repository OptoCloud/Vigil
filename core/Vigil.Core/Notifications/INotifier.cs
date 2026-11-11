using Vigil.Core.Alerting;
using Vigil.Core.Protocol;

namespace Vigil.Core.Notifications;

/// <summary>
/// One way of reaching a person: ntfy today, the self-hosted SMS gateway next. Each channel
/// says how bad something must be to use it, so a costly or intrusive one (a text at 3 am) can
/// carry only what is critical while ntfy carries warnings too.
/// </summary>
/// <remarks>
/// Adding a channel is: implement this, register it in <c>VigilServices</c> behind its own
/// environment variables, and give it a minimum severity. <see cref="AlertRouter"/> decides
/// what reaches it, including the all-clears. <see cref="Send"/> must return at once and never
/// throw: the runtime's loop calls it.
/// </remarks>
public interface INotifier
{
    /// <summary>For the log, never the address: a topic's name or a phone number is private.</summary>
    public string Name { get; }

    /// <summary>The least severe event this channel is used for. Never <see cref="Severity.Info"/>.</summary>
    public Severity MinimumSeverity { get; }

    public void Send(Notification notification);
}

/// <summary>
/// An event to deliver. <see cref="IsAllClear"/> marks the info event that clears something
/// this channel was told about, which reads differently ("resolved") from a new problem.
/// </summary>
public sealed record Notification(NewEvent Event, bool IsAllClear);
