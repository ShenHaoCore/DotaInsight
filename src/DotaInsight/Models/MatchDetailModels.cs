using System.Text.Json.Serialization;
using DotaInsight.Helpers;

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

    /// <summary>经济优势曲线（天辉 − 夜魇，逐分钟）。缺失/未解析为空。</summary>
    public IReadOnlyList<int?>? GoldAdvantage { get; init; }

    /// <summary>经验优势曲线（天辉 − 夜魇，逐分钟）。缺失为空。</summary>
    public IReadOnlyList<int?>? XpAdvantage { get; init; }

    /// <summary>眼位布置（观察/岗哨，UV 已换算到 0-127 地图网格）。无记录为空。</summary>
    public IReadOnlyList<MatchWardPlacement> Wards { get; init; } = [];

    /// <summary>Ban/Pick 序列（队长模式 2 / 队长征召 16）。其他模式为空。</summary>
    public IReadOnlyList<MatchDraftStep> DraftSteps { get; init; } = [];

    /// <summary>是否技能征召（OMG，模式 18）。</summary>
    public bool IsAbilityDraft => GameMode == 18;

    /// <summary>建筑（塔/兵营/遗迹）与推倒时间线。无 objectives 数据为空。</summary>
    public IReadOnlyList<MatchBuilding> Buildings { get; init; } = [];

    /// <summary>时间轴事件（推塔/兵营/肉山/一血/击杀）。无数据为空。</summary>
    public IReadOnlyList<MatchMapEvent> MapEvents { get; init; } = [];

    /// <summary>天辉活动热区（lane_pos 聚合，"x,y" → 出现次数）。无数据为空。</summary>
    public IReadOnlyDictionary<string, int> RadiantHeatmap { get; init; } = new Dictionary<string, int>();

    /// <summary>夜魇活动热区。无数据为空。</summary>
    public IReadOnlyDictionary<string, int> DireHeatmap { get; init; } = new Dictionary<string, int>();

    /// <summary>小地图有可看的分层数据（眼位或建筑事件）。</summary>
    public bool HasMapLayers => Wards.Count > 0 || Buildings.Count > 0;

    /// <summary>比赛已被 Valve 解析、能拿到逐人数据。未解析时只有基本战况。</summary>
    public bool HasPlayers => Radiant.Count > 0 || Dire.Count > 0;

    [JsonIgnore]
    public string DurationText => Helpers.TimeFormatting.FormatDuration(DurationSeconds);

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
/// Ban/Pick 序列中的一步（队长模式 / 队长征召）。
/// </summary>
public sealed class MatchDraftStep
{
    public int Order { get; init; }

    public bool IsPick { get; init; }

    /// <summary>team=0 为天辉，1 为夜魇（Steam API 约定）。</summary>
    public bool IsRadiant { get; init; }

    public int HeroId { get; init; }

    public string HeroName { get; init; } = string.Empty;

    public string HeroIconUrl { get; init; } = string.Empty;

    [JsonIgnore]
    public string Tooltip => $"{(IsPick ? "Pick" : "Ban")} · {HeroName} · 第 {Order + 1} 手";
}

/// <summary>
/// 小地图建筑（塔/兵营/遗迹）。坐标为 OpenDota 官方 minimap 覆盖层的百分比
/// （7.33+ 地形，odota/web BuildingMap/buildingData733），直接乘以画布边长定位。
/// </summary>
public sealed class MatchBuilding
{
    public string Id { get; set; } = string.Empty;

    /// <summary>objectives 里的 NPC 单位名（npc_dota_goodguys_tower1_top 等）。</summary>
    public string NpcKey { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public bool IsRadiant { get; set; }

    public bool IsTower { get; set; }

    public bool IsBarracks { get; set; }

    public bool IsAncient { get; set; }

    public double LeftPct { get; set; }

    public double TopPct { get; set; }

    /// <summary>被摧毁时刻（秒）；null = 终局仍在。</summary>
    public int? FallsAt { get; set; }

    /// <summary>最后一击来源的显示名（英雄中文名 / 攻城兵 / 小兵）。</summary>
    public string? KilledBy { get; set; }

    /// <summary>
    /// 建筑在时刻 t 是否还立着。t &lt; 0 表示「全部时间」，按终局状态展示
    /// （与 OpenDota 建筑图一致）；其余时刻 t ≥ FallsAt 即已倒下。
    /// </summary>
    public bool IsStandingAt(int t)
        => t < 0 ? FallsAt is null : (FallsAt is null || t < FallsAt.Value);
}

/// <summary>时间轴事件类型。</summary>
public enum MatchMapEventKind
{
    Tower,

    Barracks,

    Roshan,

    Aegis,

    FirstBlood,

    TeamFight,

    Kill
}

/// <summary>
/// 小地图时间轴事件。IsRadiant 为事件受益方：推塔/反补=动作方，肉山/盾/击杀=击杀方。
/// </summary>
public sealed class MatchMapEvent
{
    public int Time { get; init; }

    public MatchMapEventKind Kind { get; init; }

    public bool IsRadiant { get; init; }

    public string Label { get; init; } = string.Empty;
}

/// <summary>
/// 眼位布置点。UV 为换算后的 0-127 地图网格坐标
/// （odota/web utility.tsx gameCoordToUV：u = x−64，v = 191−y；
/// 世界坐标原点在左下，图像原点在左上，v 轴做了反转）。
/// </summary>
public sealed class MatchWardPlacement
{
    public double UvX { get; init; }

    public double UvY { get; init; }

    /// <summary>布置时刻（秒，负数为开局前）。</summary>
    public int Time { get; init; }

    /// <summary>消失时刻（秒，按 ehandle 配对消失日志）；无记录为 null（按满时长推算）。</summary>
    public int? LeftTime { get; init; }

    public bool IsObserver { get; init; }

    public bool IsRadiant { get; init; }

    /// <summary>
    /// 该眼位在时刻 t（秒）是否可见。t &lt; 0 表示「全部时间」（恒可见）。
    /// 语义与 odota/web alive() 一致：t ≥ 布置时刻，且未到消失时刻（无消失记录则一直存活）。
    /// </summary>
    public bool IsVisibleAt(int t)
        => t < 0 || (t >= Time && (LeftTime == null || t < LeftTime.Value));
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

    /// <summary>技能征召（OMG）选到的技能内部名，按槽位序；非 OMG 为空。</summary>
    public IReadOnlyList<string> DraftedAbilities { get; init; } = [];

    /// <summary>技能征召技能的图标 URL（与 DraftedAbilities 一一对应，不可解析的名为空串保持对齐）。</summary>
    [JsonIgnore]
    public IReadOnlyList<string> DraftedAbilityIconUrls
        => DraftedAbilities.Select(HeroAssetHelper.GetAbilityIconUrl).ToList();

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
