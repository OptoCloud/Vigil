namespace Vigil.Core.Configuration;

/// <summary>
/// vigil-core's bounds whose values something else depends on (docs/STYLE.md §4.1).
/// </summary>
/// <remarks>
/// Only <see cref="StreamSilenceTimeout"/> still crosses a language boundary: it must outlast
/// vigild's keepalive, which daemon/contract.json publishes and DaemonContractTests checks.
/// Everything else here is vigil-core's own since the Rust core and vigil-web's server merged
/// into this process.
/// </remarks>
public static class Limits
{
    /// <summary>
    /// How long the browser's live stream may stay silent before a keepalive is sent, which is
    /// also what notices a browser that has gone away and frees its slot.
    /// </summary>
    public static readonly TimeSpan StreamKeepalive = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How long vigild's stream may go without a byte before it is treated as dead. More than
    /// twice vigild's keepalive, so one keepalive lost to a slow moment is not read as a
    /// disconnect.
    /// </summary>
    public static readonly TimeSpan StreamSilenceTimeout = TimeSpan.FromSeconds(50);

    /// <summary>
    /// The most one event on vigild's stream may be (docs/SECURITY.md §4.1). A snapshot of
    /// thirteen drives and a pool is tens of KiB; the SSE parser buffers an event until its
    /// blank line arrives, so with no cap a peer that never sends one grows it without end.
    /// The same 1 MiB vigild allows a child process's output, which a snapshot is built from.
    /// </summary>
    public const long MaxDaemonEventBytes = 1024 * 1024;

    /// <summary>
    /// How long vigild may take to answer the request with headers. Its handler answers at
    /// once; a peer that accepts and then says nothing is not vigild.
    /// </summary>
    public static readonly TimeSpan DaemonResponseTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long the runtime's loop may go without turning before it counts as stuck, for the
    /// status route and the heartbeat. It turns every second; the one thing that holds it is a
    /// database statement, which <see cref="DatabaseStatementTimeout"/> ends, and a turn can run
    /// a handful of those in a row.
    /// </summary>
    public static readonly TimeSpan LoopStalledAfter = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Inputs the runtime may have queued before the pollers wait for it (docs/SECURITY.md
    /// §4.2). The loop falls behind only while the database stalls it; at vigild's 2s and the
    /// UPS's 5s this is about twenty minutes of that, far past the schema's first fill, and a
    /// poller that waits loses nothing. <see cref="Runtime.LiveHub"/> lets browsers queue at
    /// most its own cap of these, so requests cannot use it up.
    /// </summary>
    public const int RuntimeBacklog = 1024;

    /// <summary>
    /// Database queries the API runs at once, and how many requests may wait for one
    /// (docs/SECURITY.md §4.2). Each waits at most <see cref="DatabaseStatementTimeout"/>, so
    /// together they bound what unauthenticated requests can ask of the shared Postgres; past
    /// both a request is refused with 503. A page asks for three things at once.
    /// </summary>
    public const int DatabaseQueryConcurrency = 4;

    /// <inheritdoc cref="DatabaseQueryConcurrency"/>
    public const int DatabaseQueryQueue = 16;

    /// <summary>
    /// Connections Kestrel holds open at once. Kestrel's own default is unlimited; this is
    /// several times <see cref="Runtime.LiveHub.MaxSubscribers"/>, so a full set of live
    /// streams still leaves room for every page's own requests.
    /// </summary>
    public const int MaxConnections = 1024;

    /// <summary>
    /// What one client may ask in a burst, and how fast that allowance refills (ADR-023). A
    /// page load is some forty files and three API calls, and a browser retries a dropped
    /// live stream every few seconds; this is several page loads at once and then one every
    /// two seconds, which no person reaches and a loop does at once. Past it: 429.
    /// </summary>
    public const int ClientRequestBurst = 200;

    /// <inheritdoc cref="ClientRequestBurst"/>
    public const int ClientRequestsPerSecond = 20;

    /// <summary>
    /// How long a connection may take to send its request headers, and how long an idle one
    /// is kept. Kestrel's defaults (30s and 130s) are generous to a client that opens
    /// connections and says nothing; a browser needs neither.
    /// </summary>
    public static readonly TimeSpan RequestHeadersTimeout = TimeSpan.FromSeconds(10);

    /// <inheritdoc cref="RequestHeadersTimeout"/>
    public static readonly TimeSpan KeepAliveTimeout = TimeSpan.FromSeconds(60);

    /// <summary>The dashboard's port, and the one the web dev server proxies /api to.</summary>
    public const int CoreDefaultPort = 3001;

    /// <summary>Only ever used in development; production sets DATABASE_URL.</summary>
    public const string DatabaseUrlDefault = "postgres://vigil@localhost/vigil";

    /// <summary>No query should outlive a page load (docs/SECURITY.md §6).</summary>
    public static readonly TimeSpan DatabaseStatementTimeout = TimeSpan.FromSeconds(15);
}
