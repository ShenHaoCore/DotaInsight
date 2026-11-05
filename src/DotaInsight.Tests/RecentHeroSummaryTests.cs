using DotaInsight.Models;

namespace DotaInsight.Tests;

/// <summary>
/// 「近期常用英雄」聚合逻辑：分组、排序稳定性、截断与 KDA 口径。
/// </summary>
public class RecentHeroSummaryTests
{
    private static RecentMatchItem Match(int heroId, string name, bool win, int k = 3, int d = 2, int a = 5)
        => new()
        {
            HeroId = heroId,
            HeroName = name,
            IconUrl = $"https://cdn.test/{name}.png",
            IsWin = win,
            Kills = k,
            Deaths = d,
            Assists = a
        };

    [Fact]
    public void Summarize_GroupsByHero_AndSumsGamesAndWins()
    {
        var matches = new[]
        {
            Match(1, "敌法师", true),
            Match(1, "敌法师", true),
            Match(1, "敌法师", false),
            Match(7, "隐刺", false)
        };

        var list = RecentHeroSummary.Summarize(matches);

        Assert.Equal(2, list.Count);
        var am = list[0];
        Assert.Equal(1, am.HeroId);
        Assert.Equal(3, am.Games);
        Assert.Equal(2, am.Wins);
        Assert.Equal(1, am.Losses);
        Assert.Equal(66.7, am.WinRate, 1);
        Assert.Equal("敌法师", am.HeroName);
    }

    [Fact]
    public void Summarize_OrdersByGamesThenWinsThenHeroId_Stable()
    {
        // 2 场 1 胜（heroId 20）与 2 场 1 胜（heroId 5）并列 → HeroId 升序
        // 3 场 1 胜的排最前，1 场的排最后
        var matches = new[]
        {
            Match(20, "乙", true),
            Match(5, "甲", false),
            Match(20, "乙", false),
            Match(5, "甲", true),
            Match(30, "丙", true),
            Match(30, "丙", false),
            Match(30, "丙", false),
            Match(99, "丁", false)
        };

        var list = RecentHeroSummary.Summarize(matches, top: 10);

        Assert.Equal([30, 5, 20, 99], list.Select(s => s.HeroId));
    }

    [Fact]
    public void Summarize_TopLimit_KeepsMostPlayed()
    {
        var matches = Enumerable.Range(1, 12)
            .Select(i => Match(i, $"英雄{i}", true))
            .ToList();

        var list = RecentHeroSummary.Summarize(matches, top: 8);

        Assert.Equal(8, list.Count);
        Assert.Equal(1, list[0].HeroId);
    }

    [Fact]
    public void Summarize_EmptyOrInvalidInput_ReturnsEmpty()
    {
        Assert.Empty(RecentHeroSummary.Summarize([]));

        // top 非正数 → 不展示
        Assert.Empty(RecentHeroSummary.Summarize([Match(1, "敌法师", true)], top: 0));

        // HeroId 无效的行直接丢弃
        var invalid = new[] { Match(0, "未知", true), Match(-3, "未知", true) };
        Assert.Empty(RecentHeroSummary.Summarize(invalid));
    }

    [Fact]
    public void Summarize_AvgKda_UsesPerMatchKdaRatio_WithZeroDeathRule()
    {
        // 10/0/5 → KdaRatio = 15（0 死亡规则）；5/5/5 → 2.0；平均 8.5
        var matches = new[]
        {
            Match(1, "敌法师", true, k: 10, d: 0, a: 5),
            Match(1, "敌法师", true, k: 5, d: 5, a: 5)
        };

        var summary = Assert.Single(RecentHeroSummary.Summarize(matches));

        Assert.Equal(8.5, summary.AvgKda, 6);
        Assert.Equal("8.50", summary.AvgKdaText);
    }
}
