using DotaInsight.Models;

namespace DotaInsight.Helpers;

/// <summary>
/// 每日游玩热力图构建（纯函数，可测）：对局开始时间 → 按本地日期聚合计数 →
/// 周对齐（周一为首）的 53 列日历，逐格分级 0-4。
/// </summary>
public static class PlayerActivityBuilder
{
    private const int WeekColumns = 53;

    public static PlayerActivityHeatmap Build(IEnumerable<long> startTimesUtc, DateTime todayLocal)
    {
        var counts = new Dictionary<DateTime, int>();
        foreach (var t in startTimesUtc)
        {
            var day = DateTimeOffset.FromUnixTimeSeconds(t).LocalDateTime.Date;
            counts[day] = counts.GetValueOrDefault(day) + 1;
        }

        var today = todayLocal.Date;
        // 当前周的周一（周一为首的列）
        var currentWeekMonday = today.AddDays(-((int)today.DayOfWeek + 6) % 7);
        var startMonday = currentWeekMonday.AddDays(-(WeekColumns - 1) * 7);

        var weeks = new List<PlayerActivityWeek>(WeekColumns);
        var total = 0;
        var activeDays = 0;
        var best = 0;
        DateTime? bestDate = null;
        string? lastMonthLabel = null;

        for (var w = 0; w < WeekColumns; w++)
        {
            var monday = startMonday.AddDays(w * 7);

            // 月标签：该周的周一在本月前 7 天内（每周含当月 1 日的列打标），首个周始终打标
            string? monthLabel = null;
            if (w == 0 || monday.Day <= 7)
            {
                var label = $"{monday.Month}月";
                if (label != lastMonthLabel)
                {
                    monthLabel = label;
                    lastMonthLabel = label;
                }
            }

            var days = new List<PlayerActivityDay>(7);
            for (var d = 0; d < 7; d++)
            {
                var date = monday.AddDays(d);
                if (date > today)
                {
                    // 未来格：透明占位、无悬浮
                    days.Add(new PlayerActivityDay { Date = date, Level = -1 });
                    continue;
                }

                var count = counts.GetValueOrDefault(date);
                total += count;
                if (count > 0)
                {
                    activeDays++;
                }

                if (count > best)
                {
                    best = count;
                    bestDate = date;
                }

                days.Add(new PlayerActivityDay
                {
                    Date = date,
                    Count = count,
                    Level = LevelFor(count),
                    Tooltip = count > 0
                        ? $"{date:yyyy-MM-dd} · {count} 局"
                        : $"{date:yyyy-MM-dd} · 未游玩"
                });
            }

            weeks.Add(new PlayerActivityWeek { MonthLabel = monthLabel, Days = days });
        }

        var summary = $"近 12 个月 {total} 局 · 活跃 {activeDays} 天"
            + (best > 0 && bestDate is not null ? $" · 最多一天 {best} 局（{bestDate:MM-dd}）" : string.Empty);

        return new PlayerActivityHeatmap
        {
            Weeks = weeks,
            TotalMatches = total,
            ActiveDays = activeDays,
            BestDayCount = best,
            SummaryText = summary
        };
    }

    /// <summary>分级：0 局=L0；1=L1；2-3=L2；4-5=L3；≥6=L4（休闲玩家的合理密度）。</summary>
    public static int LevelFor(int count) => count switch
    {
        <= 0 => 0,
        1 => 1,
        <= 3 => 2,
        <= 5 => 3,
        _ => 4
    };
}
