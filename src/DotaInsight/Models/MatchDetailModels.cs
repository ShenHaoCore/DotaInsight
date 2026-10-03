using System.Text.Json.Serialization;

namespace DotaInsight.Models;

/// <summary>
/// 单场比赛详情：双方阵容与每名玩家的核心数据。
/// </summary>
public sealed class MatchDetail
{
    public long MatchId { get; init; }

    public bool RadiantWin { get; init; }

    public int DurationSeconds { get; init; }

    public DateTime StartTimeLocal { get; init; }

    public int GameMode { get; init; }

    public int LobbyType { get; init; }

    public int RadiantScore { get; init; }

    public int DireScore { get; init; }

    /// <summary>职业比赛队名，路人局为空。</summary>
    public string? RadiantTeamName { get; init; }

    public string? DireTeamName { get; init; }

    public IReadOnlyList<MatchPlayerItem> Radiant { get; init; } = [];

    public IReadOnlyList<MatchPlayerItem> Dire { get; init; } = [];

    /// <summary>比赛已被 Valve 解析、能拿到逐人数据。未解析时只有基本战况。</summary>
    public bool HasPlayers => Radiant.Count > 0 || Dire.Count > 0;

    [JsonIgnore]
    public string DurationText
    {
        get
        {
            var span = TimeSpan.FromSeconds(Math.Max(0, DurationSeconds));
            return span.TotalHours >= 1
                ? span.ToString(@"h\:mm\:ss")
                : span.ToString(@"mm\:ss");
        }
    }

    [JsonIgnore]
    public string StartTimeText => StartTimeLocal.ToString("yyyy-MM-dd HH:mm");

    [JsonIgnore]
    public string ScoreText => $"{RadiantScore} : {DireScore}";

    [JsonIgnore]
    public string GameModeText => FormatGameMode(GameMode);

    [JsonIgnore]
    public string LobbyTypeText => FormatLobbyType(LobbyType);

    [JsonIgnore]
    public string RadiantResultText => RadiantWin ? "胜利" : "失败";

    [JsonIgnore]
    public string DireResultText => RadiantWin ? "失败" : "胜利";

    [JsonIgnore]
    public string RadiantTeamDisplay =>
        string.IsNullOrWhiteSpace(RadiantTeamName) ? "天辉" : RadiantTeamName!;

    [JsonIgnore]
    public string DireTeamDisplay =>
        string.IsNullOrWhiteSpace(DireTeamName) ? "夜魇" : DireTeamName!;

    private static string FormatGameMode(int mode) => mode switch
    {
        1 => "全阵营选择",
        2 => "队长模式",
        3 => "随机征召",
        4 => "单一征召",
        5 => "全随机",
        11 => "中路单挑",
        12 => "最少使用英雄",
        16 => "队长征召",
        17 => "平衡征召",
        18 => "技能征召",
        20 => "全随机死亡竞赛",
        21 => "1v1 中路",
        22 => "天梯全阵营选择",
        23 => "加速模式",
        _ => "未知模式"
    };

    private static string FormatLobbyType(int lobby) => lobby switch
    {
        0 => "普通对局",
        1 => "练习赛",
        2 => "锦标赛",
        4 => "人机合作",
        5 => "战队比赛",
        6 => "单排",
        7 => "天梯",
        8 => "1v1 中路",
        9 => "勇士联赛",
        _ => "普通对局"
    };
}

/// <summary>
/// 比赛中的一名玩家。
/// </summary>
public sealed class MatchPlayerItem
{
    public long AccountId { get; init; }

    public int HeroId { get; init; }

    public string HeroName { get; init; } = string.Empty;

    public string HeroIconUrl { get; init; } = string.Empty;

    /// <summary>玩家昵称，匿名或未公开时为空。</summary>
    public string PersonaName { get; init; } = string.Empty;

    public int Level { get; init; }

    public int Kills { get; init; }

    public int Deaths { get; init; }

    public int Assists { get; init; }

    public int GoldPerMin { get; init; }

    public int XpPerMin { get; init; }

    public int LastHits { get; init; }

    public int Denies { get; init; }

    public int NetWorth { get; init; }

    public int HeroDamage { get; init; }

    public int HeroHealing { get; init; }

    public int TowerDamage { get; init; }

    /// <summary>6 个主物品槽的图标 URL，空槽位为空字符串（保持栏位对齐）。</summary>
    public IReadOnlyList<string> ItemIconUrls { get; init; } = [];

    /// <summary>中立物品图标 URL，未装备时为空。</summary>
    public string NeutralItemIconUrl { get; init; } = string.Empty;

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(PersonaName)
        ? "匿名玩家"
        : PersonaName;

    [JsonIgnore]
    public string KdaText => $"{Kills} / {Deaths} / {Assists}";

    [JsonIgnore]
    public double Kda =>
        Deaths == 0 ? Kills + Assists : (Kills + Assists) / (double)Deaths;

    [JsonIgnore]
    public string KdaRatioText => Kda.ToString("F2");

    [JsonIgnore]
    public string LevelText => $"Lv {Level}";

    [JsonIgnore]
    public string LastHitsText => $"{LastHits}/{Denies}";

    [JsonIgnore]
    public string NetWorthText => NetWorth >= 1000
        ? $"{NetWorth / 1000.0:F1}k"
        : NetWorth.ToString();

    [JsonIgnore]
    public string HeroDamageText => HeroDamage >= 1000
        ? $"{HeroDamage / 1000.0:F1}k"
        : HeroDamage.ToString();

    [JsonIgnore]
    public string HeroHealingText => HeroHealing >= 1000
        ? $"{HeroHealing / 1000.0:F1}k"
        : HeroHealing.ToString();
}
