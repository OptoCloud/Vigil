using Vigil.Core.Alerting;
using Vigil.Core.Notifications;
using Vigil.Core.Protocol;
using Xunit;

namespace Vigil.Core.Tests;

public sealed class AlertRouterTests
{
    private sealed class Channel(string name, Severity minimum) : INotifier
    {
        public List<Notification> Sent { get; } = [];

        public string Name => name;

        public Severity MinimumSeverity => minimum;

        public void Send(Notification notification) => Sent.Add(notification);
    }

    private readonly Channel _ntfy = new("ntfy", Severity.Warning);
    private readonly Channel _sms = new("sms", Severity.Critical);

    private AlertRouter Router() => new([_ntfy, _sms]);

    [Fact]
    public void EachChannelGetsWhatMeetsItsMinimum()
    {
        var router = Router();

        router.Route(new NewEvent(Severity.Warning, "zfs-spares:tank", "one spare short"));
        router.Route(new NewEvent(Severity.Critical, "zfs-pool:tank", "void is DEGRADED"));

        Assert.Equal(["zfs-spares:tank", "zfs-pool:tank"], _ntfy.Sent.Select(sent => sent.Event.Kind));
        Assert.Equal(["zfs-pool:tank"], _sms.Sent.Select(sent => sent.Event.Kind));
    }

    [Fact]
    public void TheAllClearGoesToExactlyTheChannelsThatHeardTheProblem()
    {
        var router = Router();
        router.Route(new NewEvent(Severity.Warning, "zfs-spares:tank", "one spare short"));
        router.Route(new NewEvent(Severity.Critical, "zfs-pool:tank", "void is DEGRADED"));

        router.Route(new NewEvent(Severity.Info, "zfs-spares:tank", "spares back"));
        router.Route(new NewEvent(Severity.Info, "zfs-pool:tank", "void is ONLINE"));

        Assert.Equal([false, false, true, true], _ntfy.Sent.Select(sent => sent.IsAllClear));
        Assert.Equal([false, true], _sms.Sent.Select(sent => sent.IsAllClear));
    }

    [Fact]
    public void InfoAboutNothingPushedStaysInTheLogAndAnAllClearIsSentOnce()
    {
        var router = Router();

        router.Route(new NewEvent(Severity.Info, "zfs-resilver:tank", "finished, no errors"));
        Assert.Empty(_ntfy.Sent);

        router.Route(new NewEvent(Severity.Critical, "daemon-lost", "vigild is unreachable"));
        router.Route(new NewEvent(Severity.Info, "daemon-lost", "reachable again"));
        router.Route(new NewEvent(Severity.Info, "daemon-lost", "reachable again"));
        Assert.Equal(2, _ntfy.Sent.Count);
    }
}
