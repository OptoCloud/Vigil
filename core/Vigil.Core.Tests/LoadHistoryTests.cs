using System.Globalization;
using Npgsql;
using Vigil.Core.Data;
using Vigil.Core.Protocol;
using Xunit;

namespace Vigil.Core.Tests;

/// <summary>The host-load rows (ADR-025): written by <see cref="LoadWriter"/>, read back by <see cref="HistoryQueries"/>.</summary>
[Collection(DatabaseTestGroup.Name)]
public sealed class LoadHistoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    private static HostLoad Load(DateTimeOffset at, double read, double? cpuBusy = 20) => new()
    {
        AsOf = Rfc3339.From(at),
        IntervalSeconds = 10,
        UptimeSeconds = 1_000,
        Memory = new MemoryLoad
        {
            TotalBytes = 64L << 30,
            UsedBytes = 40L << 30,
            CachedBytes = 6L << 30,
            ArcBytes = null,
            ArcMaxBytes = null,
            SwapTotalBytes = 0,
            SwapUsedBytes = 0,
        },
        Cpu = cpuBusy is { } busy ? new CpuLoad { BusyPercent = busy, IowaitPercent = 1.5, LogicalCpus = 16 } : null,
        Load = new LoadAverage { One = 1.25, Five = 1, Fifteen = 0.75 },
        Pressure = new PressureLoad
        {
            CpuPercent = 1,
            MemoryPercent = null,
            IoPercent = 20,
            CpuFullPercent = null,
            MemoryFullPercent = null,
            IoFullPercent = 4,
        },
        Disks = [new DiskLoad { DeviceName = "naa.1", Port = null, ReadBytesPerSecond = read, WrittenBytesPerSecond = 1_000, BusyPercent = 12.5 }],
        Pools = [new PoolLoad { Name = "tank", ReadBytesPerSecond = read * 8, WrittenBytesPerSecond = 8_000 }],
        Interfaces = [new InterfaceLoad { Name = "vmbr0", ReceivedBytesPerSecond = 500, TransmittedBytesPerSecond = 250 }],
    };

    private static ZfsStatus Pools(DateTimeOffset at, ulong allocated) => new()
    {
        IsAvailable = true,
        AsOf = Rfc3339.From(at),
        Pools =
        [
            new PoolStatus { Name = "tank", State = "ONLINE", ErrorCount = 0, SizeBytes = 1_000, AllocatedBytes = allocated, Scan = null, Vdevs = [] },
            // zpool could not size this one: no row, rather than a 0 % that reads as empty.
            new PoolStatus { Name = "odd", State = "ONLINE", ErrorCount = 0, SizeBytes = null, AllocatedBytes = 5, Scan = null, Vdevs = [] },
        ],
    };

    private static async Task<long> CountAsync(NpgsqlDataSource source, string sql, CancellationToken token)
    {
        await using var command = source.CreateCommand(sql);
        return Convert.ToInt64(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);
    }

    [Fact]
    public void TrafficIsStoredUnderOneIdPerDiskPoolAndInterface()
    {
        var rows = LoadWriter.TrafficRows(Load(Now, 2_000_000));

        Assert.Equal(["disk:naa.1", "pool:tank", "net:vmbr0"], rows.Select(row => row.Id));
        Assert.Equal((500d, 250d, (double?)null), (rows[2].In, rows[2].Out, rows[2].Busy));
        Assert.Equal(12.5, rows[0].Busy);
    }

    [Fact]
    public async Task WritesOneHostRowItsTrafficAndEachSizedPool()
    {
        var token = TestContext.Current.CancellationToken;
        await using var databases = await DatabaseFixture.ConnectAsync(token);
        await DatabaseFixture.TruncateAsync(databases.Writer, token);
        var writer = new LoadWriter(databases);

        await writer.InsertHostSampleAsync(Load(Now, 2_000_000, cpuBusy: null), token);
        await writer.InsertHostSampleAsync(Load(Now, 2_000_000, cpuBusy: null), token);
        await writer.InsertPoolSamplesAsync(Pools(Now, 250), token);

        // A retry after a blip does not double-count, and an unknown rate is null, not zero.
        Assert.Equal(1, await CountAsync(databases.Writer, "select count(*) from host_samples where cpu_busy is null and arc is null", token));
        Assert.Equal(1, await CountAsync(databases.Writer, "select count(*) from host_samples where uptime_seconds = 1000 and arc_max is null", token));
        Assert.Equal(3, await CountAsync(databases.Writer, "select count(*) from traffic_samples", token));
        Assert.Equal(1, await CountAsync(databases.Writer, "select count(*) from traffic_samples where busy_percent is not null", token));
        Assert.Equal(1, await CountAsync(databases.Writer, "select count(*) from pool_samples where pool = 'tank' and allocated_bytes = 250", token));
        Assert.Equal(0, await CountAsync(databases.Writer, "select count(*) from pool_samples where pool = 'odd'", token));
    }

    [Fact]
    public async Task ReadsTheRawRowsBackAsTheChartsDrawThem()
    {
        var token = TestContext.Current.CancellationToken;
        await using var databases = await DatabaseFixture.ConnectAsync(token);
        await DatabaseFixture.TruncateAsync(databases.Writer, token);
        var writer = new LoadWriter(databases);

        // Both inside one 10-second bucket of the 1h range, which averages them.
        await writer.InsertHostSampleAsync(Load(Now.AddMinutes(-5), 1_000_000), token);
        await writer.InsertHostSampleAsync(Load(Now.AddMinutes(-5).AddSeconds(5), 3_000_000, cpuBusy: 40), token);
        await writer.InsertPoolSamplesAsync(Pools(Now.AddMinutes(-5), 255), token);
        // Outside the range.
        await writer.InsertHostSampleAsync(Load(Now.AddHours(-2), 9_000_000), token);

        var history = await new HistoryQueries(databases).QueryAsync(RangeKey.OneHour, upsName: null, Now, token);

        Assert.Equal(30, Assert.Single(history.Host["cpuBusy"])[1]);
        Assert.Equal(40L << 30, Assert.Single(history.Host["memoryUsed"])[1]);
        Assert.Equal(1.25, Assert.Single(history.Host["load1"])[1]);
        Assert.False(history.Host.ContainsKey("arc"), "a series with no value at all is left out");
        // What the rest is out of goes in beside it, so a percent of history divides by its own day's total.
        Assert.Equal(64L << 30, Assert.Single(history.Host["memoryTotal"])[1]);
        Assert.Equal(4, Assert.Single(history.Host["pressureIoFull"])[1]);
        Assert.False(history.Host.ContainsKey("pressureMemoryFull"));
        Assert.Equal(2_000_000, Assert.Single(history.TrafficIn["disk:naa.1"])[1]);
        Assert.Equal(16_000_000, Assert.Single(history.TrafficIn["pool:tank"])[1]);
        Assert.Equal(250, Assert.Single(history.TrafficOut["net:vmbr0"])[1]);
        Assert.Equal(["disk:naa.1"], history.Busy.Keys);
        Assert.Equal(25.5, Assert.Single(history.PoolFill["tank"])[1]);
    }

    [Fact]
    public async Task ReadsTheRollupsWeightedByTheirSampleCounts()
    {
        var token = TestContext.Current.CancellationToken;
        await using var databases = await DatabaseFixture.ConnectAsync(token);
        await DatabaseFixture.TruncateAsync(databases.Writer, token);
        var writer = new LoadWriter(databases);

        // One minute with one quiet row, the next with five busy ones: weighted, not 50/50.
        var first = Now.AddHours(-3);
        await writer.InsertHostSampleAsync(Load(first, 0, cpuBusy: 10), token);
        for (var i = 0; i < 5; i++)
        {
            await writer.InsertHostSampleAsync(Load(first.AddMinutes(1).AddSeconds(i * 10), 6_000_000, cpuBusy: 70), token);
        }

        await writer.InsertPoolSamplesAsync(Pools(first, 500), token);
        foreach (var view in new[] { "host_1m", "traffic_1m", "pool_1m" })
        {
            // sql-literal-ok: `view` is one of the three literals just above.
            await using var refresh = databases.Writer.CreateCommand($"call refresh_continuous_aggregate('{view}', null, null)");
            await refresh.ExecuteNonQueryAsync(token);
        }

        var history = await new HistoryQueries(databases).QueryAsync(RangeKey.OneDay, upsName: null, Now, token);

        Assert.Equal(60, Assert.Single(history.Host["cpuBusy"])[1]);
        Assert.Equal(5_000_000, Assert.Single(history.TrafficIn["disk:naa.1"])[1]);
        Assert.Equal(50, Assert.Single(history.PoolFill["tank"])[1]);
    }
}
