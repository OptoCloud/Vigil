using Vigil.Core.Api;
using Vigil.Core.Protocol;
using Vigil.Core.Runtime;
using Xunit;

namespace Vigil.Core.Tests;

public sealed class MetricsTests
{
    internal static Snapshot Sample { get; } = new()
    {
        TimestampUtc = "2026-10-01T12:00:00.000Z",
        Sensors =
        [
            new SensorReading { Id = "cpu", Category = SensorCategory.Cpu, Label = "Tctl", CelsiusOrNull = 38.25, SourcePath = "p", IsAvailable = true },
            // Asleep: unknown, so it must not be written as 0 °C.
            new SensorReading { Id = "drive:naa.1", Category = SensorCategory.Drive, Label = "d", CelsiusOrNull = null, SourcePath = "p", IsAvailable = false },
        ],
        Fans = [new FanStatus { Id = "drive-cage", DutyPercent = 65, Rpm = null, Mode = PwmMode.Manual, Stalled = false }],
        DriveHealth = [],
        ControlLoopHealthy = true,
    };

    private static string Rendered(RuntimeView view, bool connected = true)
    {
        var pulse = new RuntimePulse(new ManualTime());
        pulse.Record(connected, databaseReady: false, view);
        return Metrics.Render(pulse);
    }

    [Fact]
    public void WritesWhatIsKnownAndLeavesOutWhatIsNot()
    {
        var alerts = new[] { new Alert { Kind = "k", Severity = Severity.Critical, Message = "m", Since = "s" } };
        var text = Rendered(new RuntimeView(Sample, [], null, alerts, null));

        Assert.Contains("vigil_daemon_connected 1\n", text, StringComparison.Ordinal);
        Assert.Contains("vigil_database_ready 0\n", text, StringComparison.Ordinal);
        Assert.Contains("vigil_alerts{severity=\"critical\"} 1\n", text, StringComparison.Ordinal);
        Assert.Contains("vigil_alerts{severity=\"warning\"} 0\n", text, StringComparison.Ordinal);
        Assert.Contains("vigil_temperature_celsius{sensor=\"cpu\"} 38.25\n", text, StringComparison.Ordinal);
        Assert.Contains("vigil_fan_duty_percent{fan=\"drive-cage\"} 65\n", text, StringComparison.Ordinal);
        Assert.Contains("vigil_build_info{version=\"development\"} 1\n", text, StringComparison.Ordinal);

        // A sleeping drive and an unreadable tach are absent, not zero.
        Assert.DoesNotContain("drive:naa.1", text, StringComparison.Ordinal);
        Assert.DoesNotContain("vigil_fan_rpm", text, StringComparison.Ordinal);
        // No UPS, no host load: no series, rather than a row of zeroes.
        Assert.DoesNotContain("vigil_ups_", text, StringComparison.Ordinal);
        Assert.DoesNotContain("vigil_host_", text, StringComparison.Ordinal);
    }

    [Fact]
    public void BeforeTheFirstTurnItStillAnswers()
    {
        var text = Metrics.Render(new RuntimePulse(new ManualTime()));

        Assert.Contains("vigil_daemon_connected 0\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("vigil_loop_age_seconds", text, StringComparison.Ordinal);
    }

    [Fact]
    public void EachMetricIsDescribedOnceAndLabelValuesAreEscaped()
    {
        var text = new PrometheusText();
        text.Gauge("m", "help", 1.5, ("name", "a\"b\\c\nd"));
        text.Gauge("m", "help", 2, ("name", "plain"), ("other", "x"));
        text.Gauge("m", "help", (double?)null, ("name", "unknown"));
        text.Gauge("m", "help", double.NaN, ("name", "nan"));

        Assert.Equal(
            "# HELP m help\n# TYPE m gauge\nm{name=\"a\\\"b\\\\c\\nd\"} 1.5\nm{name=\"plain\",other=\"x\"} 2\n",
            text.ToString());
    }
}
