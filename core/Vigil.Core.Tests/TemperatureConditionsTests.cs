using Vigil.Core.Alerting;
using Vigil.Core.Protocol;
using Xunit;

namespace Vigil.Core.Tests;

public sealed class TemperatureConditionsTests
{
    private static SensorReading Sensor(string id, SensorCategory category, double? celsius) => new()
    {
        Id = id,
        Category = category,
        Label = "label",
        CelsiusOrNull = celsius,
        SourcePath = "path",
        IsAvailable = celsius is not null,
    };

    /// <summary>What is visible with these sensors, given what is raised already.</summary>
    private static Conditions Judge(SensorReading[] sensors, Dictionary<string, Severity>? raised = null, TemperatureLimits? limits = null)
    {
        var conditions = new Conditions();
        var snapshot = new Snapshot { TimestampUtc = "t", Sensors = sensors, Fans = [], DriveHealth = [], ControlLoopHealthy = true };
        TemperatureConditions.AddTo(conditions, snapshot, limits ?? TemperatureLimits.Defaults, kind => raised?.TryGetValue(kind, out var held) == true ? held : null);
        return conditions;
    }

    private static Dictionary<string, Severity> Raised(string sensor, Severity at) =>
        new(StringComparer.Ordinal) { [TemperatureConditions.KindOf(sensor)] = at };

    [Fact]
    public void ASensorUnderItsLimitIsNotAProblem()
    {
        // What this host reads on a warm day, each well inside its default.
        SensorReading[] sensors =
        [
            Sensor("cpu", SensorCategory.Cpu, 74), Sensor("board", SensorCategory.BoardAmbient, 35), Sensor("drive:naa.1", SensorCategory.Drive, 45),
            Sensor("gpu", SensorCategory.Gpu, 50), Sensor("dimm:0", SensorCategory.Memory, 37), Sensor("hba", SensorCategory.Hba, 57),
        ];

        Assert.Empty(Judge(sensors));
    }

    [Theory]
    [InlineData(49.9, null)]
    [InlineData(50, Severity.Warning)]
    [InlineData(59.9, Severity.Warning)]
    [InlineData(60, Severity.Critical)]
    public void EachLimitIsReachedAtItsOwnTemperature(double celsius, Severity? expected)
    {
        var conditions = Judge([Sensor("drive:naa.1", SensorCategory.Drive, celsius)]);

        Assert.Equal(expected, conditions.GetValueOrDefault("temperature:drive:naa.1")?.Severity);
    }

    [Fact]
    public void TheMessageSaysWhichSensorHowHotAndAtWhatLimit()
    {
        var condition = Judge([Sensor("hba", SensorCategory.Hba, 86.4)])["temperature:hba"];

        Assert.Equal("Sensor 'hba' (label) is at 86 °C (critical at 85 °C).", condition.Message);
        Assert.Equal("Sensor 'hba' (label) is back under 75 °C.", condition.Cleared);
    }

    [Theory]
    // A warning lets go two degrees under its limit, not at it.
    [InlineData(Severity.Warning, 49, Severity.Warning)]
    [InlineData(Severity.Warning, 47.9, null)]
    // A critical eases to a warning two degrees under the critical limit.
    [InlineData(Severity.Critical, 58.5, Severity.Critical)]
    [InlineData(Severity.Critical, 57.9, Severity.Warning)]
    public void ARaisedLevelHoldsUntilTheSensorIsTwoDegreesUnderIt(Severity held, double celsius, Severity? expected)
    {
        var conditions = Judge([Sensor("drive:naa.1", SensorCategory.Drive, celsius)], Raised("drive:naa.1", held));

        Assert.Equal(expected, conditions.GetValueOrDefault("temperature:drive:naa.1")?.Severity);
    }

    [Fact]
    public void AHotSensorThatStopsAnsweringStaysRaisedAndAColdOneRaisesNothing()
    {
        SensorReading[] unreadable = [Sensor("drive:naa.1", SensorCategory.Drive, null), Sensor("drive:naa.2", SensorCategory.Drive, null)];

        var conditions = Judge(unreadable, Raised("drive:naa.1", Severity.Critical));

        var held = Assert.Single(conditions);
        Assert.Equal("temperature:drive:naa.1", held.Key);
        Assert.Equal(Severity.Critical, held.Value.Severity);
        Assert.Contains("can no longer be read", held.Value.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LimitsFromTheEnvironmentReplaceOnlyTheCategoriesTheyName()
    {
        Assert.True(TemperatureLimits.TryParse(" drive:40/45 , hba: 60 / 70.5 ", out var limits, out _));

        Assert.Equal(new TemperatureLimit(40, 45), limits.For(SensorCategory.Drive));
        Assert.Equal(new TemperatureLimit(60, 70.5), limits.For(SensorCategory.Hba));
        Assert.Equal(TemperatureLimits.Defaults.For(SensorCategory.Cpu), limits.For(SensorCategory.Cpu));
        Assert.Equal(Severity.Critical, Judge([Sensor("drive:naa.1", SensorCategory.Drive, 46)], limits: limits)["temperature:drive:naa.1"].Severity);
    }

    [Fact]
    public void EveryCategoryVigildKnowsHasADefault()
    {
        Assert.All(Enum.GetValues<SensorCategory>(), category => Assert.NotNull(TemperatureLimits.Defaults.For(category)));
        Assert.True(TemperatureLimits.TryParse(null, out _, out _));
        Assert.True(TemperatureLimits.TryParse(string.Empty, out _, out _));
    }

    [Theory]
    [InlineData("drive:50", "category:warn/critical")]
    [InlineData("drive:50/sixty", "category:warn/critical")]
    [InlineData("drive:50/500", "category:warn/critical")]
    [InlineData("drive:-5/60", "category:warn/critical")]
    [InlineData("disk:50/60", "not one of")]
    [InlineData("drive:60/50", "warning comes first")]
    [InlineData("drive:50/60,drive:51/61", "twice")]
    public void ABadLimitIsRefusedWithAReason(string text, string reason)
    {
        Assert.False(TemperatureLimits.TryParse(text, out _, out var problem));
        Assert.Contains(reason, problem, StringComparison.Ordinal);
    }
}
