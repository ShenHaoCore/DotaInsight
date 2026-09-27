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

    public string ProfileUrl { get; init; } = string.Empty;

    public int? RankTier { get; init; }

    public int Wins { get; init; }

    public int Losses { get; init; }

    public int TotalMatches => Wins + Losses;

    public double WinRate => TotalMatches > 0 ? Wins * 100.0 / TotalMatches : 0;

    public string WinRateText => $"{WinRate:F1}%";

    public string RecordText => $"{Wins}胜 / {Losses}负";

    public string RankText => RankTier is null or 0 ? "未定级" : FormatRank(RankTier.Value);

    private static string FormatRank(int rankTier)
    {
        // rank_tier: 十位=段位(1-8)，个位=星级(1-5)
        var medal = rankTier / 10;
        var stars = rankTier % 10;
        var name = medal switch
        {
            1 => "纹章",
            2 => "卫士",
            3 => "中军",
            4 => "统将",
            5 => "传奇",
            6 => "万古",
            7 => "超凡",
            8 => "冠绝",
            _ => "未知"
        };
        return stars > 0 ? $"{name} {stars}" : name;
    }
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

    public string ResultText => IsWin ? "胜利" : "失败";

    public string KdaText => $"{Kills}/{Deaths}/{Assists}";

    public double KdaRatio => Deaths == 0 ? Kills + Assists : (Kills + Assists) / (double)Deaths;

    public string KdaRatioText => $"{KdaRatio:F2}";

    public string DurationText => $"{DurationSeconds / 60}:{DurationSeconds % 60:D2}";

    public string SideText => IsRadiant ? "天辉" : "夜魇";

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

/// <summary>OpenDota recentMatches DTO。</summary>
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

    [JsonPropertyName("kills")]
    public int Kills { get; set; }

    [JsonPropertyName("deaths")]
    public int Deaths { get; set; }

    [JsonPropertyName("assists")]
    public int Assists { get; set; }
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
