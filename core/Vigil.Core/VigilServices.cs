using System.Net;
using System.Net.Mail;
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

        if (startup.SmsUrl is { Length: > 0 } sms && startup.SmsToken is { Length: > 0 } smsToken)
        {
            // One long-lived HttpClient, with pooled connections recycled so a DNS change for the
            // gateway is picked up; the alternative, IHttpClientFactory, is the same thing with
            // more parts for a client that sends a handful of requests a month.
            services.AddSingleton(_ => new SmsClient(
                new HttpClient(new SocketsHttpHandler
                {
                    PooledConnectionLifetime = TimeSpan.FromMinutes(15),
                    ConnectTimeout = SmsClient.SendTimeout,
                    MaxResponseHeadersLength = SmsClient.MaximumHeaderKilobytes,
                }),
                new Uri(sms),
                smsToken));
            services.AddChannel<SmsClient>();
        }

        if (startup.SmtpHost is { Length: > 0 } smtpHost && startup.EmailFrom is { Length: > 0 } emailFrom)
        {
            services.AddSingleton(_ => new EmailClient(new EmailSettings
            {
                Host = smtpHost,
                Port = startup.SmtpPort,
                StartTls = startup.SmtpStartTls,
                Credentials = startup.SmtpUsername is { Length: > 0 } username ? new NetworkCredential(username, startup.SmtpPassword) : null,
                From = new MailAddress(emailFrom),
                To = MailAddresses.Parse(startup.EmailTo),
            }));
            services.AddChannel<EmailClient>();
        }

        // The runtime's one input. Bounded: the loop stops reading while the database stalls it
        // (the schema's first fill takes minutes), and everything queued meanwhile is memory.
        // A full queue makes a poller wait and refuses a new live stream.
        services.AddSingleton(_ => Channel.CreateBounded<RuntimeInput>(
            new BoundedChannelOptions(Limits.RuntimeBacklog) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait }));
        services.AddSingleton<RuntimePulse>();
        services.AddSingleton(provider => new LiveHub(provider.GetRequiredService<Channel<RuntimeInput>>().Writer));
        services.AddSingleton(provider => new VigilRuntime(
            provider.GetRequiredService<IOptions<VigilOptions>>().Value,
            provider.GetRequiredService<IRuntimeStore>(),
            provider.GetServices<INotifier>().ToList(),
            provider.GetRequiredService<ILogger<VigilRuntime>>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<RuntimePulse>()));

        services.AddHostedService<RuntimeService>();
        services.AddHostedService<DaemonFeed>();
        if (startup.NutHost is { Length: > 0 })
        {
            services.AddHostedService<UpsFeed>();
        }

        if (startup.HeartbeatUrl is { Length: > 0 } heartbeat)
        {
            services.AddHostedService(provider => new Heartbeat(
                new HttpClient(new SocketsHttpHandler
                {
                    PooledConnectionLifetime = TimeSpan.FromMinutes(15),
                    ConnectTimeout = Heartbeat.SendTimeout,
                    MaxResponseHeadersLength = Heartbeat.MaximumHeaderKilobytes,
                }),
                new Uri(heartbeat),
                TimeSpan.FromSeconds(startup.HeartbeatIntervalSeconds),
                provider.GetRequiredService<RuntimePulse>(),
                provider.GetRequiredService<ILogger<Heartbeat>>(),
                provider.GetRequiredService<TimeProvider>()));
        }

        return services;
    }

    /// <summary>
    /// A channel is its <see cref="IDelivery"/> behind a <see cref="RetryingNotifier"/>, which
    /// the container owns and stops with the process.
    /// </summary>
    private static void AddChannel<TDelivery>(this IServiceCollection services)
        where TDelivery : class, IDelivery =>
        services.AddSingleton<INotifier>(provider => new RetryingNotifier(
            provider.GetRequiredService<TDelivery>(),
            provider.GetRequiredService<ILogger<RetryingNotifier>>(),
            provider.GetRequiredService<TimeProvider>()));
}
