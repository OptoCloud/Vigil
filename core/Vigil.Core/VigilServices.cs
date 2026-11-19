using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Vigil.Core.Clients;
using Vigil.Core.Configuration;
using Vigil.Core.Data;
using Vigil.Core.Notifications;
using Vigil.Core.Runtime;

namespace Vigil.Core;

/// <summary>How vigil-core is put together. Program.cs only calls this and starts it.</summary>
public static class VigilServices
{
    public static IServiceCollection AddVigilCore(this IServiceCollection services, VigilOptions startup)
    {
        services.AddSingleton(TimeProvider.System);

        services.AddSingleton<VigilDataSources>();
        services.AddSingleton<SchemaSetup>();
        services.AddSingleton<SampleWriter>();
        services.AddSingleton<LoadWriter>();
        services.AddSingleton<DriveStore>();
        services.AddSingleton<ZfsStore>();
        services.AddSingleton<EventStore>();
        services.AddSingleton<HistoryQueries>();
        services.AddSingleton<IRuntimeStore, DatabaseRuntimeStore>();

        services.AddSingleton<VigildClient>();
        services.AddSingleton<NutClient>();

        if (startup.NtfyUrl is { Length: > 0 } ntfy)
        {
            // One long-lived HttpClient, with pooled connections recycled so a DNS change for the
            // ntfy host is picked up; the alternative, IHttpClientFactory, is the same thing with
            // more parts for a client that sends a handful of requests a month.
            services.AddSingleton(provider => new NtfyClient(
                new HttpClient(new SocketsHttpHandler
                {
                    PooledConnectionLifetime = TimeSpan.FromMinutes(15),
                    ConnectTimeout = NtfyClient.SendTimeout,
                    MaxResponseHeadersLength = NtfyClient.MaximumHeaderKilobytes,
                }),
                new Uri(ntfy),
                provider.GetRequiredService<ILogger<NtfyClient>>()));
            services.AddSingleton<INotifier>(provider => provider.GetRequiredService<NtfyClient>());
        }

        if (startup.SmsUrl is { Length: > 0 } sms && startup.SmsToken is { Length: > 0 } smsToken)
        {
            // Built as ntfy's is, and for the same reasons.
            services.AddSingleton(provider => new SmsClient(
                new HttpClient(new SocketsHttpHandler
                {
                    PooledConnectionLifetime = TimeSpan.FromMinutes(15),
                    ConnectTimeout = SmsClient.SendTimeout,
                    MaxResponseHeadersLength = SmsClient.MaximumHeaderKilobytes,
                }),
                new Uri(sms),
                smsToken,
                provider.GetRequiredService<ILogger<SmsClient>>()));
            services.AddSingleton<INotifier>(provider => provider.GetRequiredService<SmsClient>());
        }

        // The runtime's one input. Bounded: the loop stops reading while the database stalls it
        // (the schema's first fill takes minutes), and everything queued meanwhile is memory.
        // A full queue makes a poller wait and refuses a new live stream.
        services.AddSingleton(_ => Channel.CreateBounded<RuntimeInput>(
            new BoundedChannelOptions(Limits.RuntimeBacklog) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait }));
        services.AddSingleton(provider => new LiveHub(provider.GetRequiredService<Channel<RuntimeInput>>().Writer));
        services.AddSingleton(provider => new VigilRuntime(
            provider.GetRequiredService<IOptions<VigilOptions>>().Value,
            provider.GetRequiredService<IRuntimeStore>(),
            provider.GetServices<INotifier>().ToList(),
            provider.GetRequiredService<ILogger<VigilRuntime>>(),
            provider.GetRequiredService<TimeProvider>()));

        services.AddHostedService<RuntimeService>();
        services.AddHostedService<DaemonFeed>();
        if (startup.NutHost is { Length: > 0 })
        {
            services.AddHostedService<UpsFeed>();
        }

        return services;
    }
}
