using Microsoft.Extensions.Options;
using Vigil.Core;
using Vigil.Core.Api;
using Vigil.Core.Configuration;

// vigil-core: vigil's orchestrator. Reads vigild and the UPS, records history in Postgres,
// raises alerts, streams the live state, and serves the dashboard. Configured entirely from the
// environment; runs until killed.
//
// See docs/ARCHITECTURE.md. vigild, which drives the fans as root, stays Rust.

var builder = WebApplication.CreateBuilder(args);

// sd_notify readiness and the journald console format, so `journalctl -p warning` works. Under
// systemd this replaces both hand-rolled pieces the Rust version needed; run by hand it is a
// no-op and logging falls back to the plain console.
builder.Host.UseSystemd();

// Environment only, as the Rust version was, under the names already in
// core/deploy/vigil-core.env. See VigilEnvironment for why the mapping is explicit.
builder.Configuration.AddVigilEnvironment();

builder.Services
    .AddOptions<VigilOptions>()
    .Bind(builder.Configuration)
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<VigilOptions>, VigilOptionsValidator>();

var startup = VigilEnvironment.Read(builder.Configuration);
var listen = VigilOptionsValidator.ListenAddress(startup);
if (listen is not null)
{
    builder.WebHost.UseUrls(listen);
}

builder.WebHost.ConfigureKestrel(kestrel =>
{
    // Nothing gains from knowing which server this is.
    kestrel.AddServerHeader = false;

    // Kestrel's default is no limit at all (docs/SECURITY.md §4.2).
    kestrel.Limits.MaxConcurrentConnections = Limits.MaxConnections;

    // Every route is a GET, so nothing reads a request body. At zero Kestrel does not drain
    // one either: a request that brings a body is answered and its connection closed.
    kestrel.Limits.MaxRequestBodySize = 0;

    kestrel.Limits.RequestHeadersTimeout = Limits.RequestHeadersTimeout;
    kestrel.Limits.KeepAliveTimeout = Limits.KeepAliveTimeout;
});

// vigil-core logs state changes, not requests, as the Rust core did: a page polling history and
// holding a live stream would otherwise bury the one line that says a fan stalled.
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

builder.Services.AddVigilCore(startup);
builder.Services.AddAdmission(startup);

var app = builder.Build();

// In this order: whose request it is, the headers every answer carries (a refusal included),
// then the limits, so that a file costs a client's allowance as an API call does.
app.UseForwardedClient(app.Services.GetRequiredService<TrustedProxies>());
app.UseSecurityHeaders();
app.UseRateLimiter();
if (startup.WebRoot is { Length: > 0 } webRoot)
{
    app.UseDashboard(webRoot);
    app.MapDashboardPages(webRoot);
}
else
{
    app.Logger.LogInformation("WEB_ROOT is not set: serving the API only");
}

app.MapVigilApi();

app.Run();
