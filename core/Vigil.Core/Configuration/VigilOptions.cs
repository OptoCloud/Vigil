using System.ComponentModel.DataAnnotations;

namespace Vigil.Core.Configuration;

/// <summary>
/// Everything vigil-core is configured with, all from the environment, as the Rust version was
/// (/etc/vigil-core.env in production).
/// </summary>
/// <remarks>
/// Settable, not <c>init</c>, and it has to stay that way. NativeAOT binds configuration with a
/// source generator, and the code it generates cannot set an <c>init</c> property: it skipped
/// every one without a warning, so the native build started on all defaults, the database
/// password and the listen address included. <c>VigilEnvironmentTests</c> catches a regression,
/// because it binds through <see cref="VigilEnvironment.Read"/>, which is compiled here.
/// </remarks>
public sealed class VigilOptions
{
    /// <summary>vigild's unix socket, bind-mounted into the container.</summary>
    public string? VigildSocket { get; set; }

    /// <summary>host:port of a TCP stand-in. Development only, against dev/mock-vigild.mjs.</summary>
    public string? VigildUrl { get; set; }

    /// <summary>The role that owns the schema and writes every row.</summary>
    [Required]
    public string DatabaseUrl { get; set; } = Limits.DatabaseUrlDefault;

    /// <summary>
    /// A SELECT-only role for the read path (history, the event log). Optional: unset, the read
    /// path shares <see cref="DatabaseUrl"/> and startup says so.
    /// </summary>
    /// <remarks>
    /// This exists because vigil-core and vigil-web are one process now. The separation phase 1
    /// built relied on them being two, with only the writer holding a write credential; one
    /// address space cannot give that back, but a SELECT-only role still means a bug in a query
    /// path cannot write. docs/SECURITY.md §3.4.
    /// </remarks>
    public string? DatabaseUrlReadonly { get; set; }

    /// <summary>vigild publishes every 2s. That resolution matters live, not in history.</summary>
    public double PersistIntervalSeconds { get; set; } = 10;

    public double RawRetentionDays { get; set; } = 30;

    /// <summary>Unset leaves the UPS out entirely rather than showing it as broken.</summary>
    public string? NutHost { get; set; }

    public int NutPort { get; set; } = 3493;

    /// <summary>The UPS's name on upsd ([apc] in ups.conf), which its history is stored under.</summary>
    public string NutUps { get; set; } = "apc";

    /// <summary>
    /// Optional: the same UPS under a second name on upsd, read when the first cannot be (a
    /// second driver on another cable). Unset, an unreadable UPS is simply unreadable.
    /// </summary>
    public string? NutUpsFallback { get; set; }

    public double NutPollSeconds { get; set; } = 5;

    /// <summary>
    /// How long the host takes to shut down once upsmon starts it, guests included. Optional;
    /// with it the dashboard shows what the UPS's <c>battery.runtime.low</c> leaves to spare,
    /// and an alert says when it leaves nothing.
    /// </summary>
    public double? HostShutdownSeconds { get; set; }

    /// <summary>
    /// °C. A lead-acid battery's life roughly halves for every 8 to 10 °C it spends above 25 °C;
    /// 40 is where that becomes worth acting on rather than watching.
    /// </summary>
    public double UpsBatteryTemperatureWarn { get; set; } = 40;

    /// <summary>
    /// Where each kind of sensor is too hot, as <c>category:warn/critical</c> entries in °C
    /// (<c>drive:50/60,cpu:85/95</c>). A category not named keeps its default; see
    /// <c>Alerting.TemperatureLimits</c> for those.
    /// </summary>
    public string? TemperatureLimits { get; set; }

    /// <summary>Optional, with <see cref="SmsToken"/>: the SMS gateway's endpoint (ADR-027). Never logged.</summary>
    public string? SmsUrl { get; set; }

    /// <summary>The gateway's bearer token. A secret like DATABASE_URL: never logged, never in an error.</summary>
    public string? SmsToken { get; set; }

    /// <summary>
    /// Optional, with <see cref="EmailFrom"/> and <see cref="EmailTo"/>: the mail server that
    /// alerts are handed to (ADR-028). Like every address here, never logged.
    /// </summary>
    public string? SmtpHost { get; set; }

    /// <summary>587 is the submission port, where STARTTLS is expected.</summary>
    public int SmtpPort { get; set; } = 587;

    /// <summary>
    /// Upgrade to TLS before logging in. Off only for a relay on this machine or the LAN that
    /// offers none; the validator refuses a password without it.
    /// </summary>
    public bool SmtpStartTls { get; set; } = true;

    /// <summary>Optional, with <see cref="SmtpPassword"/>: the mail server's login.</summary>
    public string? SmtpUsername { get; set; }

    /// <summary>A secret like DATABASE_URL: never logged, never in an error.</summary>
    public string? SmtpPassword { get; set; }

    /// <summary>The address alerts are sent from.</summary>
    public string? EmailFrom { get; set; }

    /// <summary>Who alerts are sent to, separated by commas.</summary>
    public string? EmailTo { get; set; }

    /// <summary>
    /// Optional: a URL fetched every <see cref="HeartbeatIntervalSeconds"/> while vigil-core is
    /// working, for a service that alerts when the requests stop. It is that check's only
    /// secret: never logged.
    /// </summary>
    public string? HeartbeatUrl { get; set; }

    public double HeartbeatIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// The one address vigil-core listens on, which serves the dashboard: every interface by
    /// default, and the address the TLS proxy reaches in production (ADR-023). There is
    /// deliberately no second, private listener (ADR-017).
    /// </summary>
    public string CoreHost { get; set; } = "0.0.0.0";

    public int CorePort { get; set; } = Limits.CoreDefaultPort;

    /// <summary>
    /// The reverse proxies in front of vigil-core, separated by commas: IP addresses, networks
    /// (<c>10.0.0.0/24</c>), or <c>cloudflare</c> for the ranges Cloudflare publishes, which are
    /// then kept current. Only a connection from one of these has its <c>X-Forwarded-For</c> and <c>X-Forwarded-Proto</c>
    /// believed, which is what tells one visitor from another for the per-client limits. Unset,
    /// a client is whoever opened the connection: right when nothing sits in front, and wrong
    /// behind a proxy, where every visitor would then share one client's allowance.
    /// Parsed by <see cref="Api.TrustedProxies"/>.
    /// </summary>
    public string? TrustedProxies { get; set; }

    /// <summary>
    /// The host names the dashboard answers to, separated by semicolons; a request for any
    /// other <c>Host</c> gets a 400. Unset answers to every name. This is ASP.NET's own
    /// <c>AllowedHosts</c> setting, which its host filtering reads under this same key.
    /// </summary>
    public string? AllowedHosts { get; set; }

    /// <summary>
    /// The dashboard's static build (SvelteKit's output directory). Unset serves the API only,
    /// which is what development wants: there, Vite serves the page and proxies the API.
    /// </summary>
    public string? WebRoot { get; set; }

    /// <summary>How long vigild has to be unreachable before that counts as an incident.</summary>
    public double DaemonLostAfterSeconds { get; set; } = 20;

    /// <summary>
    /// How old a pool's last completed scrub may be before that is a warning. Scrubs are monthly
    /// (Debian's zfsutils timer runs on the second Sunday), so 35 days is one missed.
    /// </summary>
    public double ZfsScrubMaxAgeDays { get; set; } = 35;

    /// <summary>
    /// Hot spares each pool should have AVAIL, as <c>pool:count</c> pairs separated by commas
    /// (<c>tank:2</c>). A pool not named is not checked. Parsed by <see cref="SpareCounts"/>.
    /// </summary>
    public string? ZfsExpectedSpares { get; set; }

    /// <summary>
    /// How old a drive's newest completed SMART self-test may be before that is a warning. Unset
    /// leaves it unjudged, which is right until smartd schedules tests on the host: otherwise
    /// every drive would be overdue. 8 suits a weekly long test.
    /// </summary>
    public double? SmartSelfTestMaxAgeDays { get; set; }
}
