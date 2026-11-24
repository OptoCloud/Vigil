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

        // The runtime and vigild's feed always; the UPS, texts and email only when configured.
        Assert.Equal(
            [nameof(RuntimeService), nameof(DaemonFeed)],
            provider.GetServices<IHostedService>().Select(service => service.GetType().Name));
        Assert.Null(provider.GetService<EmailClient>());
        Assert.Empty(provider.GetServices<INotifier>());
        Assert.Null(provider.GetService<SmsClient>());
    }

    [Fact]
    public async Task AddsTheUpsFeedAndTheChannelsOnlyWhenTheyAreConfigured()
    {
        await using var provider = Compose(new VigilOptions
        {
            VigildUrl = "http://127.0.0.1:1",
            NutHost = "192.0.2.4",
            SmtpHost = "mail.example",
            SmtpUsername = "vigil",
            SmtpPassword = "s3cret",
            EmailFrom = "vigil@example.com",
            EmailTo = "owner@example.com, second@example.com",
            SmsUrl = "http://192.0.2.2:8080/send",
            SmsToken = "token",
            HeartbeatUrl = "https://heartbeat.example/private-check",
        });

        Assert.Contains(provider.GetServices<IHostedService>(), service => service is UpsFeed);
        Assert.Contains(provider.GetServices<IHostedService>(), service => service is Heartbeat);
        Assert.NotNull(provider.GetService<SmsClient>());
        Assert.NotNull(provider.GetService<EmailClient>());
        // Each behind the queue that retries it.
        Assert.All(provider.GetServices<INotifier>(), channel => Assert.IsType<RetryingNotifier>(channel));
        Assert.Equal(["sms", "email"], provider.GetServices<INotifier>().Select(channel => channel.Name));
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
