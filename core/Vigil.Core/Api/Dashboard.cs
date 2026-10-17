using Microsoft.Extensions.FileProviders;

namespace Vigil.Core.Api;

/// <summary>
/// Serving the SvelteKit build, and the security headers every response carries. Ported from
/// web/src/hooks.server.ts; docs/SECURITY.md §8.
/// </summary>
public static class Dashboard
{
    /// <summary>
    /// The headers that are not script policy. No Strict-Transport-Security: this deployment
    /// serves plain HTTP on the LAN by decision (ADR-008), where HSTS does nothing.
    /// </summary>
    private static readonly (string Name, string Value)[] SecurityHeaders =
    [
        // JSON, SSE and HTML are served; none of it should ever be content-sniffed.
        ("X-Content-Type-Options", "nosniff"),

        // The dashboard links nowhere outward, so no referrer ever needs to leave.
        ("Referrer-Policy", "no-referrer"),

        // Belt and braces with frame-ancestors, for anything that predates CSP.
        ("X-Frame-Options", "DENY"),
        ("Cross-Origin-Opener-Policy", "same-origin"),

        // A read-only dashboard needs none of these, and a browser told so cannot be talked into them.
        ("Permissions-Policy", "accelerometer=(), camera=(), geolocation=(), gyroscope=(), magnetometer=(), microphone=(), payment=(), usb=()"),

        // Only the directives a <meta> CSP cannot carry, or that hold for every response. Script
        // and style policy come from the static build's own <meta> tag, which SvelteKit writes in
        // hash mode with the hash of each inline script it emits: a header policy is enforced
        // alongside it, so a script-src here would block those scripts whatever the meta allows.
        ("Content-Security-Policy", "frame-ancestors 'none'; object-src 'none'; base-uri 'self'; form-action 'none'"),
    ];

    /// <summary>SvelteKit content-hashes everything under here, so it never changes at a given URL.</summary>
    private const string ImmutablePrefix = "/_app/immutable/";

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            foreach (var (name, value) in SecurityHeaders)
            {
                context.Response.Headers[name] = value;
            }

            await next(context).ConfigureAwait(false);
        });

    /// <summary>
    /// The page SvelteKit renders for routes it cannot prerender, and the one route that is:
    /// a disk's own page, whose name is only known once the disk is. Written by adapter-static's
    /// <c>fallback</c> option in web/vite.config.ts, under this same name.
    /// </summary>
    public const string FallbackPage = "spa.html";

    /// <summary>
    /// Serves the build in <paramref name="webRoot"/>: <c>index.html</c> for <c>/</c>, each
    /// prerendered page under its own name (<c>/drives</c> is <c>drives.html</c>, as SvelteKit
    /// writes it), files as they are. No catch-all fallback: <see cref="MapDashboardPages"/>
    /// names the only paths that are not files, so any other unknown path is a 404, not a route.
    /// </summary>
    public static IApplicationBuilder UseDashboard(this IApplicationBuilder app, string webRoot)
    {
        var files = new PhysicalFileProvider(Path.GetFullPath(webRoot));

        // A page's address has no extension; its file does. Only a page that exists is
        // rewritten, so nothing else changes meaning, and the provider refuses "..".
        app.Use((context, next) =>
        {
            var path = context.Request.Path.Value?.TrimEnd('/');
            if (path is { Length: > 0 } && context.GetEndpoint() is null && files.GetFileInfo(path + ".html").Exists)
            {
                context.Request.Path = path + ".html";
            }

            return next(context);
        });

        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
        return app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = files,
            OnPrepareResponse = context =>
            {
                context.Context.Response.Headers.CacheControl =
                    context.Context.Request.Path.StartsWithSegments(ImmutablePrefix.TrimEnd('/'), StringComparison.Ordinal)
                        ? "public, max-age=31536000, immutable"
                        // index.html names the current hashed assets, so it must be revalidated
                        // or a browser keeps loading the previous build after an upgrade.
                        : "no-cache";
            },
        });
    }

    /// <summary>
    /// The dashboard's one dynamic route, <c>/drives/{wwn}/</c>, answered with
    /// <see cref="FallbackPage"/>, which reads the name from the URL itself. Mapped here rather
    /// than as a catch-all so every other unknown path stays a 404 (ADR-020).
    /// </summary>
    public static IEndpointRouteBuilder MapDashboardPages(this IEndpointRouteBuilder app, string webRoot)
    {
        var fallback = Path.Combine(Path.GetFullPath(webRoot), FallbackPage);
        app.MapGet("/drives/{wwn}", (HttpContext context) =>
        {
            // As index.html: it names the current hashed assets, so it must be revalidated.
            context.Response.Headers.CacheControl = "no-cache";
            return TypedResults.PhysicalFile(fallback, "text/html; charset=utf-8");
        });
        return app;
    }
}
