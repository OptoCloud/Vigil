using Vigil.Core.Alerting;
using Vigil.Core.Protocol;

namespace Vigil.Core.Notifications;

/// <summary>
/// Decides which events reach which channel. A problem goes to every channel whose minimum it
/// meets; the info event that later clears it (the same kind) goes to exactly the channels that
/// were told about the problem, so whoever was woken by a false alarm also hears that it is
/// over (docs/CLEANUP.md 7.3, decided 2026-11-11). Info events about nothing that was pushed
/// stay in the log.
/// </summary>
/// <remarks>
/// What was pushed is held in memory: an alert raised before a restart and cleared after it is
/// not followed by an all-clear. The runtime re-raises what still holds after a restart, so the
/// usual case, a problem that outlives the restart, is still paired. One owner, the runtime's
/// loop, so no locking.
/// </remarks>
public sealed class AlertRouter(IReadOnlyList<INotifier> notifiers)
{
    private readonly HashSet<(INotifier Notifier, string Kind)> _pushed = [];

    public IReadOnlyList<INotifier> Notifiers => notifiers;

    public void Route(NewEvent newEvent)
    {
        ArgumentNullException.ThrowIfNull(newEvent);

        foreach (var notifier in notifiers)
        {
            if (newEvent.Severity == Severity.Info)
            {
                if (_pushed.Remove((notifier, newEvent.Kind)))
                {
                    notifier.Send(new Notification(newEvent, IsAllClear: true));
                }
            }
            else if (newEvent.Severity >= notifier.MinimumSeverity)
            {
                _pushed.Add((notifier, newEvent.Kind));
                notifier.Send(new Notification(newEvent, IsAllClear: false));
            }
        }
    }
}
