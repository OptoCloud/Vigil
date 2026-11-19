using System.Security.Cryptography;
using System.Text;
using Vigil.Core.Data;
using Xunit;

namespace Vigil.Core.Tests;

/// <summary>Ported from the Rust core's timescale.rs tests. The database half runs in DataLayerTests.</summary>
public sealed class SchemaSetupTests
{
    [Fact]
    public void TheRefreshWindowStartsAtATimestampThatCannotCarrySql()
    {
        Assert.Equal(
            "call refresh_continuous_aggregate('sensor_1m', '2026-09-21T20:39:27.482Z'::timestamptz, null)",
            SchemaSetup.RefreshStatement("sensor_1m", 1_790_023_167_482));

        // Whatever the instant, the interpolated literal is only ever these characters, so there
        // is nothing in it a SQL parser could read as syntax.
        foreach (var millis in new long[] { 0, 1, 999, 1_790_023_167_482, 253_402_300_799_999 })
        {
            var literal = SchemaSetup.RefreshStatement("x", millis).Split('\'')[3];
            Assert.True(literal.All(c => char.IsAsciiDigit(c) || c is '-' or ':' or '.' or 'Z' or 'T'), $"{millis}: {literal}");
        }
    }

    [Fact]
    public void AggregateNamesFollowTheRawTable()
    {
        Assert.Equal("sensor_1m", SchemaSetup.AllSeries[0].NameOf("1m"));
        Assert.Equal("fan_1h", SchemaSetup.AllSeries[1].NameOf("1h"));
        Assert.Equal("ups_1m", SchemaSetup.AllSeries[2].NameOf("1m"));
        Assert.Equal(
            ["ups_detail_1m", "host_1m", "traffic_1m", "pool_1m"],
            SchemaSetup.AllSeries.Skip(3).Select(series => series.NameOf("1m")));
    }

    [Fact]
    public void EachHourlyAggregateReadsItsOwnMinuteOneAndEachBackfillItsLegacyTable()
    {
        foreach (var series in SchemaSetup.AllSeries)
        {
            Assert.Contains($"from {series.NameOf("1m")}", series.Query("1h"), StringComparison.Ordinal);
            Assert.Contains($"from {series.Raw}", series.Query("1m"), StringComparison.Ordinal);
            if (series.Legacy is not null)
            {
                Assert.Contains($"from {series.Legacy}", series.Query("backfill"), StringComparison.Ordinal);
            }
        }
    }

    /// <summary>
    /// Every continuous aggregate's definition as deployed (ADR-026). SchemaSetup creates a view
    /// only when it is missing, so an edited definition would never reach a database that has it;
    /// and rebuilding one drops the rollups whose raw rows retention already took. A metric added
    /// later is a new series. A hash changes here only for a series no release has shipped.
    /// </summary>
    private static readonly Dictionary<string, string> Deployed = new(StringComparer.Ordinal)
    {
        ["sensor_1m"] = "0edb0e02727321ac",
        ["sensor_1h"] = "da60c366bbae4511",
        ["fan_1m"] = "64e093bad71a2891",
        ["fan_1h"] = "d78fa371bd58fd95",
        ["ups_1m"] = "3cfe1a83c1329a67",
        ["ups_1h"] = "c661a1c80e43c950",
        ["ups_detail_1m"] = "6ea58f55541f480e",
        ["ups_detail_1h"] = "058187499de317b4",
        ["host_1m"] = "b09671c39f6a36c5",
        ["host_1h"] = "de02e0d85b000299",
        ["traffic_1m"] = "ec53ce922260e5b2",
        ["traffic_1h"] = "07247ea30b1270ba",
        ["pool_1m"] = "22253c61ffe5056b",
        ["pool_1h"] = "e25c04ca057ea163",
    };

    private static readonly string[] Levels = ["1m", "1h"];

    [Fact]
    public void NoDeployedAggregateIsRedefined()
    {
        var actual = SchemaSetup.AllSeries
            .SelectMany(series => Levels.Select(level => (Name: series.NameOf(level), Sql: series.Query(level))))
            .ToDictionary(
                view => view.Name,
                view => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(view.Sql.ReplaceLineEndings("\n"))))[..16],
                StringComparer.Ordinal);

        Assert.Equal(Deployed.OrderBy(pair => pair.Key, StringComparer.Ordinal), actual.OrderBy(pair => pair.Key, StringComparer.Ordinal));
    }
}
