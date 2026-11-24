using Vigil.Core.Configuration;
using Xunit;

namespace Vigil.Core.Tests;

public sealed class VigilOptionsValidatorTests
{
    private static IEnumerable<string> Problems(VigilOptions options) =>
        new VigilOptionsValidator().Validate(null, options).Failures ?? [];

    [Fact]
    public void TheDefaultsAreValid()
    {
        Assert.Empty(Problems(new VigilOptions()));
    }

    [Fact]
    public void AWebRootWithNoBuildInItFailsStartupRatherThanEveryRequest()
    {
        var empty = Directory.CreateTempSubdirectory("vigil-empty-root-");
        try
        {
            var problem = Assert.Single(Problems(new VigilOptions { WebRoot = empty.FullName }));
            Assert.Contains("WEB_ROOT", problem, StringComparison.Ordinal);

            File.WriteAllText(Path.Combine(empty.FullName, "index.html"), "<!doctype html>");
            Assert.Empty(Problems(new VigilOptions { WebRoot = empty.FullName }));
        }
        finally
        {
            empty.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("heartbeat.example/private-check", 60)]
    [InlineData("ftp://heartbeat.example/private-check", 60)]
    [InlineData("https://heartbeat.example/private-check", 5)]
    [InlineData("https://heartbeat.example/private-check", double.NaN)]
    public void ABadHeartbeatIsRefusedWithoutRepeatingItsUrl(string url, double interval)
    {
        var problem = Assert.Single(Problems(new VigilOptions { HeartbeatUrl = url, HeartbeatIntervalSeconds = interval }));

        Assert.Contains("HEARTBEAT_", problem, StringComparison.Ordinal);
        Assert.DoesNotContain("private-check", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void ABadTemperatureLimitIsRefused()
    {
        Assert.Contains("TEMPERATURE_LIMITS", Assert.Single(Problems(new VigilOptions { TemperatureLimits = "drive:60/50" })), StringComparison.Ordinal);
        Assert.Empty(Problems(new VigilOptions { TemperatureLimits = "drive:45/55,hba:70/80" }));
    }

    private static VigilOptions Email(Action<VigilOptions>? change = null)
    {
        var options = new VigilOptions
        {
            SmtpHost = "mail.example",
            SmtpUsername = "vigil-login",
            SmtpPassword = "s3cret-password",
            EmailFrom = "vigil@example.com",
            EmailTo = "owner@example.com, second@example.com",
        };
        change?.Invoke(options);
        return options;
    }

    [Fact]
    public void AWholeEmailChannelIsAccepted()
    {
        Assert.Empty(Problems(Email()));
        // A relay that asks for no login, and offers no TLS.
        Assert.Empty(Problems(Email(options => (options.SmtpUsername, options.SmtpPassword, options.SmtpStartTls) = (null, null, false))));
    }

    [Fact]
    public void PartOfAnEmailChannelIsRefusedWithoutRepeatingAnyOfIt()
    {
        VigilOptions[] partial =
        [
            Email(options => options.SmtpHost = null),
            Email(options => options.EmailFrom = null),
            Email(options => options.EmailTo = null),
            Email(options => options.SmtpPassword = null),
            Email(options => options.SmtpUsername = null),
            Email(options => options.SmtpHost = "smtp://mail.example:587"),
            Email(options => options.SmtpPort = 0),
            Email(options => options.EmailFrom = "not an address"),
            Email(options => options.EmailTo = "owner@example.com, not an address"),
            Email(options => options.EmailTo = string.Join(',', Enumerable.Repeat("owner@example.com", MailAddresses.MaximumRecipients + 1))),
            // A login is never sent in the clear.
            Email(options => options.SmtpStartTls = false),
        ];

        Assert.All(partial, options =>
        {
            var problems = Problems(options);
            Assert.NotEmpty(problems);
            Assert.All(problems, problem =>
            {
                Assert.DoesNotContain("s3cret", problem, StringComparison.Ordinal);
                Assert.DoesNotContain("vigil-login", problem, StringComparison.Ordinal);
                Assert.DoesNotContain("example.com", problem, StringComparison.Ordinal);
            });
        });
    }

    [Theory]
    [InlineData("http://192.0.2.2/private-path", null)]
    [InlineData(null, "s3cret-token")]
    public void HalfAnSmsChannelIsRefusedWithoutRepeatingEitherHalf(string? url, string? token)
    {
        var problem = Assert.Single(Problems(new VigilOptions { SmsUrl = url, SmsToken = token }));

        Assert.Contains("SMS_URL and SMS_TOKEN", problem, StringComparison.Ordinal);
        Assert.DoesNotContain("private-path", problem, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("192.0.2.2/private-path")]
    [InlineData("ftp://192.0.2.2/private-path")]
    public void AnSmsUrlThatIsNotHttpIsRefusedWithoutRepeatingIt(string url)
    {
        var problem = Assert.Single(Problems(new VigilOptions { SmsUrl = url, SmsToken = "s3cret-token" }));

        Assert.Contains("SMS_URL", problem, StringComparison.Ordinal);
        Assert.DoesNotContain("private-path", problem, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void AWholeSmsChannelPasses() =>
        Assert.Empty(Problems(new VigilOptions { SmsUrl = "https://sms.lan/send", SmsToken = "s3cret-token" }));

    [Theory]
    [InlineData(0)]
    [InlineData(-84)]
    [InlineData(double.NaN)]
    public void AShutdownTimeThatIsNotPositiveIsRefused(double seconds)
    {
        var problem = Assert.Single(Problems(new VigilOptions { HostShutdownSeconds = seconds }));

        Assert.Contains("HOST_SHUTDOWN_SECONDS", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)] // Would raise the alert forever.
    [InlineData(400)] // A typo for 40: would never raise it.
    public void ABatteryTemperatureLimitOutsideWhatABatteryCanReachIsRefused(double celsius)
    {
        var problem = Assert.Single(Problems(new VigilOptions { UpsBatteryTemperatureWarn = celsius }));

        Assert.Contains("UPS_BATTERY_TEMPERATURE_WARN", problem, StringComparison.Ordinal);
    }
}
