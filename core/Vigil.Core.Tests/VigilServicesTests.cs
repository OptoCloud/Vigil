using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Vigil.Core.Api;
using Vigil.Core.Clients;
using Vigil.Core.Configuration;
using Vigil.Core.Data;
using Vigil.Core.Notifications;
using Vigil.Core.Runtime;
using Xunit;

namespace Vigil.Core.Tests;

/// <summary>
/// The composition Program.cs uses. Every other test builds the pieces it needs by hand, so
/// without this a service missing from <see cref="VigilServices"/> would only show up when the
/// process started.
/// </summary>
public sealed class VigilServicesTests
{
    private static ServiceProvider Compose(VigilOptions options)
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.ClearProviders());
        services.AddSingleton(Options.Create(options));
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddVigilCore(options);
        services.AddAdmission(options);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    [Fact]
    public async Task ResolvesEveryServiceTheEndpointsAndLoopsNeed()
    {
        await using var provider = Compose(new VigilOptions { VigildUrl = "http://127.0.0.1:1" });

        Assert.NotNull(provider.GetRequiredService<VigilRuntime>());
        Assert.NotNull(provider.GetRequiredService<LiveHub>());
        Assert.NotNull(provider.GetRequiredService<HistoryQueries>());
        Assert.NotNull(provider.GetRequiredService<EventStore>());

        // The runtime and vigild's feed always; UPS and ntfy only when configured.
        Assert.Equal(
            [nameof(RuntimeService), nameof(DaemonFeed)],
            provider.GetServices<IHostedService>().Select(service => service.GetType().Name));
        Assert.Null(provider.GetService<NtfyClient>());
    }

    [Fact]
    public async Task AddsTheUpsFeedAndNtfyOnlyWhenTheyAreConfigured()
    {
        await using var provider = Compose(new VigilOptions
        {
            VigildUrl = "http://127.0.0.1:1",
            NutHost = "192.0.2.4",
            NtfyUrl = "https://ntfy.example/topic",
        });

        Assert.Contains(provider.GetServices<IHostedService>(), service => service is UpsFeed);
        Assert.NotNull(provider.GetService<NtfyClient>());
    }

    [Fact]
    public async Task RefreshesCloudflaresRangesOnlyWhenTrustedProxiesNamesCloudflare()
    {
        await using var named = Compose(new VigilOptions { VigildUrl = "http://127.0.0.1:1", TrustedProxies = "127.0.0.1" });
        await using var following = Compose(new VigilOptions { VigildUrl = "http://127.0.0.1:1", TrustedProxies = "cloudflare" });

        // Naming a proxy by address reaches out to nobody.
        Assert.DoesNotContain(named.GetServices<IHostedService>(), service => service is CloudflareRangesRefresh);
        Assert.Contains(following.GetServices<IHostedService>(), service => service is CloudflareRangesRefresh);
        Assert.True(following.GetRequiredService<TrustedProxies>().FollowsCloudflare);
    }
}
