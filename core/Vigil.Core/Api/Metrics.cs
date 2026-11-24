using System.Globalization;
using System.Text;
using Vigil.Core.Protocol;
using Vigil.Core.Runtime;

namespace Vigil.Core.Api;

/// <summary>
/// <c>GET /metrics</c>: what vigil-core holds right now, in Prometheus' text format, for
/// whoever would rather scrape vigil than read its page. The same state the live stream
/// publishes and nothing more (ADR-023): a drive is named by its WWN, as on the page, and
/// nothing masked there is a label here.
/// </summary>
/// <remarks>
/// Gauges only. vigil-core is not the place a counter's history lives, Prometheus is; and what
/// vigild publishes as counters has already been turned into rates (ADR-025). A value that is
/// unknown is left out, never written as zero: an absent series is how this format says so.
/// </remarks>
public static class Metrics
{
    public const string ContentType = "text/plain; version=0.0.4; charset=utf-8";

    public static string Render(RuntimePulse pulse)
    {
        ArgumentNullException.ThrowIfNull(pulse);

        var view = pulse.View;
        var text = new PrometheusText();

        text.Gauge("vigil_build_info", "The running release, as a label; always 1.", 1, ("version", VigilApi.Version));
        text.Gauge("vigil_daemon_connected", "1 while vigild's stream is connected.", pulse.DaemonConnected);
        text.Gauge("vigil_database_ready", "1 while history is being written.", pulse.DatabaseReady);
        text.Gauge("vigil_loop_age_seconds", "Seconds since the runtime's loop last turned.", pulse.LoopAge?.TotalSeconds);
        foreach (var severity in Enum.GetValues<Severity>().Where(severity => severity != Severity.Info))
        {
            text.Gauge("vigil_alerts", "Conditions raised now, by severity.", view.Alerts.Count(alert => alert.Severity == severity), ("severity", Name(severity)));
        }

        if (view.Snapshot is { } snapshot)
        {
            text.Gauge("vigil_control_loop_healthy", "1 while vigild's control loop reports healthy.", snapshot.ControlLoopHealthy);
            foreach (var sensor in snapshot.Sensors)
            {
                text.Gauge("vigil_temperature_celsius", "A sensor's temperature.", sensor.IsAvailable ? sensor.CelsiusOrNull : null, ("sensor", sensor.Id));
            }

            foreach (var fan in snapshot.Fans)
            {
                text.Gauge("vigil_fan_duty_percent", "The duty a fan header is driven at.", fan.DutyPercent, ("fan", fan.Id));
                text.Gauge("vigil_fan_rpm", "A fan's speed.", fan.Rpm, ("fan", fan.Id));
                text.Gauge("vigil_fan_stalled", "1 while a driven fan reads 0 RPM.", fan.Stalled, ("fan", fan.Id));
            }

            foreach (var pool in snapshot.Zfs?.Pools ?? [])
            {
                text.Gauge("vigil_pool_online", "1 while a pool's state is ONLINE.", pool.State == "ONLINE", ("pool", pool.Name));
                text.Gauge("vigil_pool_size_bytes", "A pool's raw size.", pool.SizeBytes, ("pool", pool.Name));
                text.Gauge("vigil_pool_allocated_bytes", "A pool's raw allocation.", pool.AllocatedBytes, ("pool", pool.Name));
                text.Gauge("vigil_pool_errors", "A pool's data errors.", pool.ErrorCount, ("pool", pool.Name));
            }
        }

        foreach (var drive in view.Drives)
        {
            (string, string) label = ("drive", drive.Wwn);
            text.Gauge("vigil_drive_smart_passed", "1 while a drive's SMART self-assessment passes.", drive.Passed, label);
            text.Gauge("vigil_drive_reallocated_sectors", "SMART attribute 5.", drive.ReallocatedSectorCount, label);
            text.Gauge("vigil_drive_pending_sectors", "SMART attribute 197.", drive.PendingSectorCount, label);
            text.Gauge("vigil_drive_power_on_hours", "SMART attribute 9.", drive.PowerOnHours, label);
        }

        if (view.Ups is { Enabled: true, Reading: { } ups })
        {
            text.Gauge("vigil_ups_on_battery", "1 while the UPS reports OB.", ups.Status.Contains("OB"));
            text.Gauge("vigil_ups_battery_charge_percent", "The UPS battery's charge.", ups.BatteryCharge);
            text.Gauge("vigil_ups_battery_runtime_seconds", "The runtime the UPS estimates.", ups.BatteryRuntimeSeconds);
            text.Gauge("vigil_ups_load_percent", "The UPS's load.", ups.Load);
            text.Gauge("vigil_ups_input_voltage", "The UPS's input voltage.", ups.InputVoltage);
        }

        if (view.Host is { } host)
        {
            text.Gauge("vigil_host_memory_total_bytes", "The host's RAM.", host.Memory?.TotalBytes);
            text.Gauge("vigil_host_memory_used_bytes", "RAM not available, the ZFS ARC included.", host.Memory?.UsedBytes);
            text.Gauge("vigil_host_arc_bytes", "The ZFS ARC's size.", host.Memory?.ArcBytes);
            text.Gauge("vigil_host_cpu_busy_percent", "CPU time not idle or waiting on I/O.", host.Cpu?.BusyPercent);
            text.Gauge("vigil_host_load1", "The 1-minute load average.", host.Load?.One);
        }

        return text.ToString();
    }

    private static string Name(Severity severity) => severity == Severity.Critical ? "critical" : "warning";
}

/// <summary>
/// The one writer of Prometheus' text exposition format (docs/STYLE.md §3.1): a metric's
/// <c># HELP</c> and <c># TYPE</c> once, before its first sample, and label values escaped.
/// Samples of one metric must be written together, which is how every caller here works.
/// </summary>
public sealed class PrometheusText
{
    private readonly StringBuilder _text = new();
    private string? _current;

    /// <summary>One sample. A null <paramref name="value"/> writes nothing: unknown is absent.</summary>
    public void Gauge(string name, string help, double? value, params ReadOnlySpan<(string Name, string Value)> labels)
    {
        if (value is not { } known || !double.IsFinite(known))
        {
            return;
        }

        if (_current != name)
        {
            _text.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n');
            _text.Append("# TYPE ").Append(name).Append(" gauge\n");
            _current = name;
        }

        _text.Append(name);
        for (var i = 0; i < labels.Length; i++)
        {
            _text.Append(i == 0 ? '{' : ',').Append(labels[i].Name).Append("=\"");
            foreach (var character in labels[i].Value)
            {
                // The three characters the format escapes inside a label value.
                _text.Append(character switch { '\\' => "\\\\", '"' => "\\\"", '\n' => "\\n", _ => character.ToString() });
            }

            _text.Append('"');
        }

        _text.Append(labels.Length > 0 ? "} " : " ").Append(known.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
    }

    public void Gauge(string name, string help, bool? value, params ReadOnlySpan<(string Name, string Value)> labels) =>
        Gauge(name, help, value is { } known ? (known ? 1 : 0) : (double?)null, labels);

    public override string ToString() => _text.ToString();
}
