using System.Text.Json.Serialization;

namespace DotaInsight.Models;

/// <summary>
/// OpenDota 玩家资料摘要。
/// </summary>
public sealed class PlayerProfile
{
    public long AccountId { get; init; }

    public string PersonaName { get; init; } = string.Empty;

    public string AvatarUrl { get; init; } = string.Empty;


    public int? RankTier { get; init; }

    public int Wins { get; init; }

    public int Losses { get; init; }

    public int TotalMatches => Wins + Losses;

    public double WinRate => TotalMatches > 0 ? Wins * 100.0 / TotalMatches : 0;

    public string WinRateText => $"{WinRate:F1}%";

    public string RecordText => $"{Wins}胜 / {Losses}负";

    public string RankText => Helpers.RankTierFormatter.Format(RankTier);
}

/// <summary>
/// 单场近期战绩行。
/// </summary>
public sealed class RecentMatchItem
{
    public long MatchId { get; init; }

    public int HeroId { get; init; }

    public string HeroName { get; init; } = string.Empty;

    public string HeroInternalName { get; init; } = string.Empty;

    public string IconUrl { get; init; } = string.Empty;

    public bool IsWin { get; init; }

    public int Kills { get; init; }

    public int Deaths { get; init; }

    public int Assists { get; init; }

    public int DurationSeconds { get; init; }

    public DateTime StartTimeLocal { get; init; }

    public bool IsRadiant { get; init; }

    // ===== 打法分析字段（OpenDota recentMatches 契约列，odota/core playerFields.ts）=====
    public int Gpm { get; init; }

    public int Xpm { get; init; }

    public int HeroDamage { get; init; }

    public int TowerDamage { get; init; }

    public int HeroHealing { get; init; }

    public int LastHits { get; init; }

    /// <summary>分路：1 优势路 / 2 中路 / 3 劣势路 / 4 打野（0 未知）。</summary>
    public int LaneRole { get; init; }

    public bool IsRoaming { get; init; }

    /// <summary>对局环境（lobby 平均天梯档位），0 表示无数据。</summary>
    public int AverageRank { get; init; }

    public int PartySize { get; init; }

    public string ResultText => IsWin ? "胜利" : "失败";

    public string KdaText => $"{Kills}/{Deaths}/{Assists}";

    public double KdaRatio => Deaths == 0 ? Kills + Assists : (Kills + Assists) / (double)Deaths;

    public string KdaRatioText => $"{KdaRatio:F2}";

    public string DurationText => Helpers.TimeFormatting.FormatDuration(DurationSeconds);

    public string SideText => IsRadiant ? "天辉" : "夜魇";

    /// <summary>游戏模式枚举（22=天梯全阵营 23=加速 3=随机征召 18=技能征召…），0=未知。</summary>
    public int GameMode { get; init; }

    public string StartTimeText => StartTimeLocal.ToString("MM-dd HH:mm");
}

/// <summary>
/// 战绩分析结果。
/// </summary>
public sealed class MatchAnalysisResult
{
    public PlayerProfile? Profile { get; init; }

    public IReadOnlyList<RecentMatchItem> Matches { get; init; } = Array.Empty<RecentMatchItem>();

    public bool FromCache { get; init; }

    public bool IsOffline { get; init; }

    public bool IsEmpty => Profile is null && Matches.Count == 0;

    public int RecentWins => Matches.Count(m => m.IsWin);

    public int RecentLosses => Matches.Count(m => !m.IsWin);

    public double RecentWinRate => Matches.Count == 0 ? 0 : RecentWins * 100.0 / Matches.Count;

    public double AvgKda => Matches.Count == 0 ? 0 : Matches.Average(m => m.KdaRatio);
}

/// <summary>
/// 近期对局按英雄聚合的摘要（「近期常用英雄」卡片）。
/// 数据全部来自已加载的 recentMatches，纯客户端聚合，不发额外请求。
/// </summary>
public sealed class RecentHeroSummary
{
    public int HeroId { get; init; }

    public string HeroName { get; init; } = string.Empty;

    public string IconUrl { get; init; } = string.Empty;

    public int Games { get; init; }

    public int Wins { get; init; }

    public int Losses => Games - Wins;

    public double WinRate => Games > 0 ? Wins * 100.0 / Games : 0;

    public string WinRateText => $"{WinRate:F0}%";

    public string RecordText => $"{Wins}胜{Losses}负";

    /// <summary>该英雄场均 KDA（与整页 AvgKda 同口径：逐场 KdaRatio 取平均）。</summary>
    public double AvgKda { get; init; }

    public string AvgKdaText => $"{AvgKda:F2}";

    /// <summary>
    /// 按英雄聚合近期对局：对局数降序 → 胜场降序 → HeroId 升序（保证展示稳定）。
    /// </summary>
    /// <param name="matches">已加载的近期对局（通常最多 20 场）。</param>
    /// <param name="top">最多展示几张卡；非正数返回空列表。</param>
    public static IReadOnlyList<RecentHeroSummary> Summarize(
        IReadOnlyList<RecentMatchItem> matches,
        int top = 8)
    {
        ArgumentNullException.ThrowIfNull(matches);
        if (top <= 0 || matches.Count == 0)
        {
            return [];
        }

        var summaries = matches
            .Where(m => m.HeroId > 0)
            .GroupBy(m => m.HeroId)
            .Select(g =>
            {
                var first = g.First();
                return new RecentHeroSummary
                {
                    HeroId = g.Key,
                    HeroName = first.HeroName,
                    IconUrl = first.IconUrl,
                    Games = g.Count(),
                    Wins = g.Count(m => m.IsWin),
                    AvgKda = g.Average(m => m.KdaRatio)
                };
            })
            .OrderByDescending(s => s.Games)
            .ThenByDescending(s => s.Wins)
            .ThenBy(s => s.HeroId)
            .Take(top)
            .ToList();

        return summaries;
    }
}

/// <summary>OpenDota recentMatches DTO。</summary>
/// <summary>
/// 玩家生涯英雄胜率条目（/players/{id}/heroes，按场次排序后截取）。
/// </summary>
public sealed class PlayerHeroStat
{
    public int HeroId { get; init; }

    public string HeroName { get; init; } = string.Empty;

    public string HeroIconUrl { get; init; } = string.Empty;

    public int Games { get; init; }

    public int Wins { get; init; }

    public double WinRate => Games > 0 ? Wins * 100.0 / Games : 0;

    public string WinRateText => $"{WinRate:F1}%";

    public string GamesText => $"{Games} 场";
}

/// <summary>/players/{id}/heroes 条目。</summary>
internal sealed class PlayerHeroStatDto
{
    [JsonPropertyName("hero_id")]
    public int HeroId { get; set; }

    [JsonPropertyName("games")]
    public int Games { get; set; }

    [JsonPropertyName("win")]
    public int Wins { get; set; }
}

internal sealed class RecentMatchDto
{
    [JsonPropertyName("match_id")]
    public long MatchId { get; set; }

    [JsonPropertyName("player_slot")]
    public int PlayerSlot { get; set; }

    [JsonPropertyName("radiant_win")]
    public bool RadiantWin { get; set; }

    [JsonPropertyName("duration")]
    public int Duration { get; set; }

    [JsonPropertyName("hero_id")]
    public int HeroId { get; set; }

    [JsonPropertyName("start_time")]
    public long StartTime { get; set; }

    [JsonPropertyName("game_mode")]
    public int GameMode { get; set; }

    [JsonPropertyName("kills")]
    public int Kills { get; set; }

    [JsonPropertyName("deaths")]
    public int Deaths { get; set; }

    [JsonPropertyName("assists")]
    public int Assists { get; set; }

    [JsonPropertyName("gold_per_min")]
    public int GoldPerMin { get; set; }

    [JsonPropertyName("xp_per_min")]
    public int Xpm { get; set; }

    [JsonPropertyName("hero_damage")]
    public int HeroDamage { get; set; }

    [JsonPropertyName("tower_damage")]
    public int TowerDamage { get; set; }

    [JsonPropertyName("hero_healing")]
    public int HeroHealing { get; set; }

    [JsonPropertyName("last_hits")]
    public int LastHits { get; set; }

    [JsonPropertyName("lane_role")]
    public int LaneRole { get; set; }

    [JsonPropertyName("is_roaming")]
    public bool IsRoaming { get; set; }

    [JsonPropertyName("average_rank")]
    public int AverageRank { get; set; }

    [JsonPropertyName("party_size")]
    public int PartySize { get; set; }
}

/// <summary>OpenDota players/{id} DTO。</summary>
internal sealed class PlayerApiDto
{
    [JsonPropertyName("rank_tier")]
    public int? RankTier { get; set; }

    [JsonPropertyName("profile")]
    public PlayerProfileDto? Profile { get; set; }
}

internal sealed class PlayerProfileDto
{
    [JsonPropertyName("account_id")]
    public long AccountId { get; set; }

    [JsonPropertyName("personaname")]
    public string? PersonaName { get; set; }

    [JsonPropertyName("avatarfull")]
    public string? AvatarFull { get; set; }

    [JsonPropertyName("profileurl")]
    public string? ProfileUrl { get; set; }
}

/// <summary>OpenDota players/{id}/wl DTO。</summary>
internal sealed class PlayerWlDto
{
    [JsonPropertyName("win")]
    public int Win { get; set; }

    [JsonPropertyName("lose")]
    public int Lose { get; set; }
}
