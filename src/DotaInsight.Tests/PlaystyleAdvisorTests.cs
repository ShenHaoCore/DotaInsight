using System.Text.Json;
using DotaInsight.Helpers;
using DotaInsight.Models;
using DotaInsight.Services;

namespace DotaInsight.Tests;

/// <summary>
/// 打法规则引擎：位置感知（辅助不评补刀）、阈值边界、排序与画像聚合。
/// </summary>
public class PlaystyleAdvisorTests
{
    private static PlaystyleSample Sample(Func<PlaystyleSample, PlaystyleSample>? patch = null)
    {
        var sample = new PlaystyleSample
        {
            Games = 20,
            Wins = 11,
            AvgKills = 6,
            AvgDeaths = 5,
            AvgAssists = 10,
            AvgGpm = 480,
            AvgXpm = 520,
            AvgLastHits = 180,
            AvgHeroDamage = 12000,
            AvgTowerDamage = 2500,
            AvgHeroHealing = 0,
            AvgDurationMinutes = 38,
            SafeShare = 1.0,
            HeroPool = 5,
            PartyShare = 0.2
        };
        return patch?.Invoke(sample) ?? sample;
    }

    private static PlaystyleFinding? Find(PlaystyleReport report, string title)
        => report.Findings.FirstOrDefault(f => f.Title.Contains(title));

    [Fact]
    public void Evaluate_CoreLowGpm_ProducesImprove()
    {
        var report = PlaystyleAdvisor.Evaluate(86745912, Sample(s => s with { AvgGpm = 320 }));

        var finding = Find(report, "刷钱效率偏低");
        Assert.NotNull(finding);
        Assert.Equal(PlaystyleLevel.Improve, finding!.Level);
        Assert.Contains("320", finding.Detail);
        Assert.Contains("补刀", finding.Suggestion);
    }

    [Fact]
    public void Evaluate_SupportLowGpm_IsNotPenalized()
    {
        // 位置感知的核心用例：辅助/游走位 GPM 300 是正常代价，不能报「刷钱效率偏低」
        var report = PlaystyleAdvisor.Evaluate(86745912, Sample(s => s with
        {
            AvgGpm = 300,
            SafeShare = 0,
            RoamShare = 1.0
        }));

        Assert.Null(Find(report, "刷钱效率偏低"));
    }

    [Fact]
    public void Evaluate_SupportLowParticipation_ProducesImprove()
    {
        var report = PlaystyleAdvisor.Evaluate(86745912, Sample(s => s with
        {
            RoamShare = 1.0,
            SafeShare = 0,
            AvgKills = 5,
            AvgAssists = 4 // 助攻占比 4/9 ≈ 0.44 < 0.45
        }));

        var finding = Find(report, "参战度偏低");
        Assert.NotNull(finding);
        Assert.Equal(PlaystyleLevel.Improve, finding!.Level);
    }

    [Fact]
    public void Evaluate_HighDeaths_ScaledSeverity()
    {
        var light = PlaystyleAdvisor.Evaluate(1, Sample(s => s with { AvgDeaths = 7 }));
        var heavy = PlaystyleAdvisor.Evaluate(1, Sample(s => s with { AvgDeaths = 10 }));

        var lightFinding = Find(light, "死亡率偏高");
        var heavyFinding = Find(heavy, "死亡过于频繁");
        Assert.NotNull(lightFinding);
        Assert.NotNull(heavyFinding);
        Assert.True(heavyFinding!.Priority > lightFinding!.Priority, "死亡越严重优先级越高");
    }

    [Fact]
    public void Evaluate_HeroPool_Bounds()
    {
        var narrow = PlaystyleAdvisor.Evaluate(1, Sample(s => s with { HeroPool = 2 }));
        var wide = PlaystyleAdvisor.Evaluate(1, Sample(s => s with { HeroPool = 12 }));

        Assert.NotNull(Find(narrow, "英雄池高度集中"));
        Assert.NotNull(Find(wide, "尝试的英雄较分散"));
    }

    [Fact]
    public void Evaluate_AllHealthy_ProducesGoodFindings()
    {
        var report = PlaystyleAdvisor.Evaluate(1, Sample(s => s with
        {
            AvgDeaths = 3,
            Wins = 13 // 20 场 13 胜 → 胜率 65%
        }));

        Assert.NotNull(Find(report, "近期胜率出色"));
        Assert.True(report.GoodCount >= 1);
        Assert.DoesNotContain(report.Findings, f => f.Title.Contains("胜率偏低"));
    }

    [Fact]
    public void Evaluate_LaneProfile_ReflectsMajorityAndRanksFirst()
    {
        var report = PlaystyleAdvisor.Evaluate(1, Sample(s => s with
        {
            SafeShare = 0.4,
            MidShare = 0.5,
            OffShare = 0.1
        }));

        var profile = Find(report, "位置画像");
        Assert.NotNull(profile);
        Assert.Contains("中路", profile!.Detail);
        // 排序设计：Improve 最前，画像在 Neutral 分组里排首位（Priority 50）
        Assert.Equal("位置画像", report.Findings.First(f => f.Level == PlaystyleLevel.Neutral).Title);
        Assert.Contains("主打中路", report.SummaryText);
    }

    [Fact]
    public void Evaluate_RoamingCountsToRoamLane()
    {
        var report = PlaystyleAdvisor.Evaluate(1, Sample(s => s with
        {
            SafeShare = 0.3,
            RoamShare = 0.7
        }));

        Assert.Contains("主打游走", report.SummaryText);
        // 游走位不算核心：不应出现核心位专属的「刷钱效率」规则
        Assert.DoesNotContain(report.Findings, f => f.Title.Contains("刷钱效率"));
    }

    [Fact]
    public void Evaluate_EmptySample_ReturnsEmptyReport()
    {
        var report = PlaystyleAdvisor.Evaluate(1, null);
        Assert.True(report.IsEmpty);
        Assert.Empty(report.Findings);
        Assert.Equal("暂无对局数据", report.SummaryText);
    }

    [Fact]
    public void Evaluate_Ordering_ImproveFirstThenNeutralThenGood()
    {
        var report = PlaystyleAdvisor.Evaluate(1, Sample(s => s with
        {
            AvgDeaths = 10, // Improve
            AvgGpm = 320,   // Improve
            Wins = 13       // Good
        }));

        var levels = report.Findings.Select(f => f.Level).ToArray();
        var firstGood = Array.IndexOf(levels, PlaystyleLevel.Good);
        var firstNeutral = Array.IndexOf(levels, PlaystyleLevel.Neutral);
        var lastImprove = Array.LastIndexOf(levels, PlaystyleLevel.Improve);

        Assert.True(lastImprove < firstNeutral || firstNeutral < 0);
        Assert.True(firstNeutral < firstGood || firstGood < 0);
    }
}

/// <summary>
/// 打法样本构建：输入改为战绩分析的 RecentMatchItem（合并后同一份数据源），
/// 并保留 recentMatches snake_case 契约字段的反序列化验证。
/// </summary>
public class PlaystyleMappingTests
{
    private static RecentMatchItem Row(long matchId, int heroId, bool win, int laneRole, bool roam,
        int gpm = 480, int kills = 6, int deaths = 5, int assists = 10, int partySize = 1, int avgRank = 45)
        => new()
        {
            MatchId = matchId,
            HeroId = heroId,
            HeroName = $"英雄{heroId}",
            IsWin = win,
            Kills = kills,
            Deaths = deaths,
            Assists = assists,
            DurationSeconds = 2400,
            Gpm = gpm,
            Xpm = 520,
            HeroDamage = 12000,
            TowerDamage = 2500,
            HeroHealing = 0,
            LastHits = 180,
            LaneRole = laneRole,
            IsRoaming = roam,
            AverageRank = avgRank,
            PartySize = partySize
        };

    [Fact]
    public void BuildSample_FromRecentMatchItems_Aggregates()
    {
        var rows = new[]
        {
            Row(1001, 1, true, 1, false, gpm: 560, kills: 8, deaths: 2, partySize: 1),
            Row(1002, 1, false, 1, false, gpm: 430, kills: 4, deaths: 9, partySize: 5),
            Row(1003, 7, true, 0, true, gpm: 280, kills: 2, deaths: 6) // 游走（is_roaming 优先于 lane_role=0）
        };

        var sample = PlaystyleAdvisor.BuildSample(rows);

        Assert.NotNull(sample);
        Assert.Equal(3, sample!.Games);
        Assert.Equal(2, sample.Wins); // 1001 胜、1002 负、1003 胜
        Assert.Equal((560 + 430 + 280) / 3.0, sample.AvgGpm, 6);
        Assert.Equal((8 + 4 + 2) / 3.0, sample.AvgKills, 6);
        Assert.Equal(2.0 / 3, sample.SafeShare, 6);
        Assert.Equal(1.0 / 3, sample.RoamShare, 6);
        Assert.Equal(2, sample.HeroPool);       // heroId 1,1,7 → 去重 2 个
        Assert.Equal(1.0 / 3, sample.PartyShare, 6);
        Assert.Equal(45, sample.AverageLobbyRank!.Value, 6);
    }

    [Fact]
    public void BuildSample_EmptyOrInvalid_ReturnsNull()
    {
        Assert.Null(PlaystyleAdvisor.BuildSample([]));
        Assert.Null(PlaystyleAdvisor.BuildSample([new RecentMatchItem { MatchId = 0 }]));
    }

    [Fact]
    public void BuildReport_FromRecentMatchItems_ProducesFindings()
    {
        var rows = new[]
        {
            Row(1001, 1, true, 1, false),
            Row(1002, 1, true, 1, false),
            Row(1003, 7, true, 1, false)
        };

        var report = PlaystyleAdvisor.Evaluate(86745912, PlaystyleAdvisor.BuildSample(rows));

        Assert.False(report.IsEmpty);
        Assert.Contains("主打优势路", report.SummaryText);
        Assert.NotEmpty(report.Findings);
    }

    [Fact]
    public void RecentMatchDto_JsonContract_BindsPlaystyleFields()
    {
        // 契约来源：odota/core svc/api/playerFields.ts recentMatchesCols（snake_case）
        const string json = """
            {"match_id":1001,"player_slot":0,"radiant_win":true,"hero_id":1,"duration":2400,
             "kills":8,"deaths":2,"assists":12,"gold_per_min":560,"xp_per_min":600,
             "hero_damage":18000,"tower_damage":3000,"hero_healing":0,"last_hits":210,
             "lane_role":1,"is_roaming":false,"average_rank":45,"party_size":1}
            """;

        var dto = JsonSerializer.Deserialize<RecentMatchDto>(json, HttpCall.JsonOptions);

        Assert.NotNull(dto);
        Assert.Equal(560, dto!.GoldPerMin);
        Assert.Equal(210, dto.LastHits);
        Assert.Equal(1, dto.LaneRole);
        Assert.False(dto.IsRoaming);
        Assert.Equal(45, dto.AverageRank);
        Assert.Equal(1, dto.PartySize);
    }
}

