using DotaInsight.Helpers;

namespace DotaInsight.Tests;

/// <summary>
/// 每日游玩热力图构建：周一为首的 53 周对齐、按本地日期聚合计数、等级分档、
/// 未来格占位、月标签（每周含当月 1 日时打标且不连续重复）。
/// 锚定日期 2026-11-06（周五，与本机时区一致）。
/// </summary>
public class PlayerActivityBuilderTests
{
    private static readonly DateTime Today = new(2026, 11, 6);

    /// <summary>本地时区（UTC+8）下把本地日期转成 Unix 秒。</summary>
    private static long Utc(int year, int month, int day, int hour = 12)
        => new DateTimeOffset(year, month, day, hour, 0, 0, TimeSpan.FromHours(8)).ToUnixTimeSeconds();

    [Fact]
    public void Build_AlignsMondayColumns_AndFutureCells()
    {
        var heatmap = PlayerActivityBuilder.Build([], Today);

        Assert.Equal(53, heatmap.Weeks.Count);
        Assert.All(heatmap.Weeks, w => Assert.Equal(7, w.Days.Count));

        // 首列为周一，末列最后一格为今天所在周的周日
        Assert.Equal(DayOfWeek.Monday, heatmap.Weeks[0].Days[0].Date.DayOfWeek);
        var lastWeek = heatmap.Weeks[^1];
        Assert.Equal(Today.AddDays(2), lastWeek.Days[6].Date); // 周日 = 今天 + 2
        Assert.Equal(-1, lastWeek.Days[6].Level);              // 未来的格子占位
        Assert.Equal(-1, lastWeek.Days[5].Level);              // 周六同样是未来
        Assert.Equal(0, lastWeek.Days[4].Level);               // 周五（今天）是真实格
    }

    [Fact]
    public void Build_AggregatesByLocalDay_AndLevels()
    {
        var times = new[]
        {
            Utc(2026, 1, 15, 1), Utc(2026, 1, 15, 12), Utc(2026, 1, 15, 23), // 同一天 3 局 → L2
            Utc(2026, 1, 16), Utc(2026, 1, 16), Utc(2026, 1, 16), Utc(2026, 1, 16), Utc(2026, 1, 16), Utc(2026, 1, 16), // 6 局 → L4
            Utc(2025, 12, 1) // 1 局 → L1
        };

        var heatmap = PlayerActivityBuilder.Build(times, Today);

        Assert.Equal(10, heatmap.TotalMatches);
        Assert.Equal(3, heatmap.ActiveDays);
        Assert.Equal(6, heatmap.BestDayCount);

        var cell315 = heatmap.Weeks.SelectMany(w => w.Days).Single(d => d.Date == new DateTime(2026, 1, 15));
        Assert.Equal(3, cell315.Count);
        Assert.Equal(2, cell315.Level);

        var cell116 = heatmap.Weeks.SelectMany(w => w.Days).Single(d => d.Date == new DateTime(2026, 1, 16));
        Assert.Equal(4, cell116.Level);
        Assert.Equal("2026-01-16 · 6 局", cell116.Tooltip);
    }

    [Fact]
    public void Build_MonthLabels_MarkWeeksContainingMonthStart()
    {
        var heatmap = PlayerActivityBuilder.Build([], Today);

        var labels = heatmap.Weeks
            .Select((w, i) => (w.MonthLabel, i))
            .Where(x => x.MonthLabel is not null)
            .ToList();

        // 有标签且相邻不重复
        Assert.NotEmpty(labels);
        for (var i = 1; i < labels.Count; i++)
        {
            Assert.NotEqual(labels[i - 1].MonthLabel, labels[i].MonthLabel);
        }

        // 1 月的标签存在，且该列的周一在 1 月 1-7 日之间
        var january = labels.Single(x => x.MonthLabel == "1月");
        var monday = heatmap.Weeks[january.i].Days[0].Date;
        Assert.True(monday.Month == 1 && monday.Day <= 7);
    }

    [Fact]
    public void Build_EmptyInput_AllZeroCells()
    {
        var heatmap = PlayerActivityBuilder.Build([], Today);

        Assert.Equal(0, heatmap.TotalMatches);
        Assert.Equal(0, heatmap.ActiveDays);
        Assert.DoesNotContain(heatmap.Weeks.SelectMany(w => w.Days), d => d.Level > 0);
        Assert.DoesNotContain(heatmap.Weeks.SelectMany(w => w.Days), d => d.Level < 0 && d.Date <= Today);
    }
}
