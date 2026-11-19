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
    [InlineData("ntfy.sh/private-topic")]
    [InlineData("ftp://ntfy.sh/private-topic")]
    public void AnNtfyUrlThatIsNotHttpIsRefusedWithoutRepeatingIt(string url)
    {
        var problem = Assert.Single(Problems(new VigilOptions { NtfyUrl = url }));

        Assert.Contains("NTFY_URL", problem, StringComparison.Ordinal);
        Assert.DoesNotContain("private-topic", problem, StringComparison.Ordinal);
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
