using System.Text.Json.Serialization;
using DotaInsight.Helpers;

namespace DotaInsight.Models;

/// <summary>
/// OpenDota /api/heroStats 英雄统计模型（含派生胜率/选取率与详情字段）。
/// </summary>
public sealed class HeroStat
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>内部名，如 npc_dota_hero_antimage。</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>本地化显示名。</summary>
    [JsonPropertyName("localized_name")]
    public string LocalizedName { get; set; } = string.Empty;

    [JsonPropertyName("primary_attr")]
    public string PrimaryAttr { get; set; } = string.Empty;

    [JsonPropertyName("attack_type")]
    public string AttackType { get; set; } = string.Empty;

    [JsonPropertyName("roles")]
    public List<string> Roles { get; set; } = [];

    [JsonPropertyName("pro_pick")]
    public int ProPick { get; set; }

    [JsonPropertyName("pro_win")]
    public int ProWin { get; set; }

    [JsonPropertyName("pro_ban")]
    public int ProBan { get; set; }

    [JsonPropertyName("pub_pick")]
    public long PubPick { get; set; }

    [JsonPropertyName("pub_win")]
    public long PubWin { get; set; }

    [JsonPropertyName("turbo_picks")]
    public long TurboPicks { get; set; }

    [JsonPropertyName("turbo_wins")]
    public long TurboWins { get; set; }

    [JsonPropertyName("base_str")]
    public int BaseStr { get; set; }

    [JsonPropertyName("base_agi")]
    public int BaseAgi { get; set; }

    [JsonPropertyName("base_int")]
    public int BaseInt { get; set; }

    [JsonPropertyName("str_gain")]
    public double StrGain { get; set; }

    [JsonPropertyName("agi_gain")]
    public double AgiGain { get; set; }

    [JsonPropertyName("int_gain")]
    public double IntGain { get; set; }

    [JsonPropertyName("base_armor")]
    public double BaseArmor { get; set; }

    [JsonPropertyName("base_mr")]
    public double BaseMagicResist { get; set; }

    [JsonPropertyName("base_health")]
    public int BaseHealth { get; set; }

    [JsonPropertyName("base_mana")]
    public int BaseMana { get; set; }

    [JsonPropertyName("base_health_regen")]
    public double BaseHealthRegen { get; set; }

    [JsonPropertyName("base_mana_regen")]
    public double BaseManaRegen { get; set; }

    [JsonPropertyName("base_attack_min")]
    public int BaseAttackMin { get; set; }

    [JsonPropertyName("base_attack_max")]
    public int BaseAttackMax { get; set; }

    [JsonPropertyName("attack_range")]
    public int AttackRange { get; set; }

    [JsonPropertyName("attack_rate")]
    public double AttackRate { get; set; }

    [JsonPropertyName("move_speed")]
    public int MoveSpeed { get; set; }

    [JsonPropertyName("turn_rate")]
    public double TurnRate { get; set; }

    [JsonPropertyName("projectile_speed")]
    public int ProjectileSpeed { get; set; }

    [JsonPropertyName("day_vision")]
    public int DayVision { get; set; }

    [JsonPropertyName("night_vision")]
    public int NightVision { get; set; }

    [JsonPropertyName("brackets")]
    public List<HeroBracketStat> Brackets { get; set; } = [];

    /// <summary>全分段选取场次（由各段位聚合或职业数据）。</summary>
    [JsonIgnore]
    public int Matches { get; set; }

    /// <summary>胜率 0–100。</summary>
    [JsonIgnore]
    public double WinRate { get; set; }

    /// <summary>选取率 0–100（相对全体英雄选取总和）。</summary>
    [JsonIgnore]
    public double PickRate { get; set; }

    [JsonIgnore]
    public string IconUrl => HeroAssetHelper.GetIconUrl(Name);

    [JsonIgnore]
    public string SmallIconUrl => HeroAssetHelper.GetSmallIconUrl(Name);

    [JsonIgnore]
    public string PrimaryAttrIcon => HeroAssetHelper.GetPrimaryAttrIcon(PrimaryAttr);

    [JsonIgnore]
    public string StrIcon => HeroAssetHelper.StrIcon;

    [JsonIgnore]
    public string AgiIcon => HeroAssetHelper.AgiIcon;

    [JsonIgnore]
    public string IntIcon => HeroAssetHelper.IntIcon;

    [JsonIgnore]
    public string HealthIcon => HeroAssetHelper.HealthIcon;

    [JsonIgnore]
    public string ManaIcon => HeroAssetHelper.ManaIcon;

    [JsonIgnore]
    public string ArmorIcon => HeroAssetHelper.ArmorIcon;

    [JsonIgnore]
    public string MagicResistIcon => HeroAssetHelper.MagicResistIcon;

    [JsonIgnore]
    public string DamageIcon => HeroAssetHelper.DamageIcon;

    [JsonIgnore]
    public string AttackTimeIcon => HeroAssetHelper.AttackTimeIcon;

    [JsonIgnore]
    public string AttackRangeIcon => HeroAssetHelper.AttackRangeIcon;

    [JsonIgnore]
    public string MoveSpeedIcon => HeroAssetHelper.MoveSpeedIcon;

    [JsonIgnore]
    public string VisionIcon => HeroAssetHelper.VisionIcon;

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(LocalizedName) ? Name : LocalizedName;

    /// <summary>英文展示名，如 Elder Titan。</summary>
    [JsonIgnore]
    public string EnglishName => HeroAssetHelper.GetEnglishDisplayName(Name);

    [JsonIgnore]
    public bool IsPrimaryStr => PrimaryAttr is "力量" or "str";

    [JsonIgnore]
    public bool IsPrimaryAgi => PrimaryAttr is "敏捷" or "agi";

    [JsonIgnore]
    public bool IsPrimaryInt => PrimaryAttr is "智力" or "int";

    [JsonIgnore]
    public bool IsPrimaryUni => PrimaryAttr is "全才" or "all" or "universal";

    [JsonIgnore]
    public string WinRateText => $"{WinRate:F1}%";

    [JsonIgnore]
    public string PickRateText => $"{PickRate:F1}%";

    [JsonIgnore]
    public string RolesText => Roles.Count == 0 ? "—" : string.Join(" · ", Roles);

    [JsonIgnore]
    public string AttackTypeText => HeroDisplayHelper.ToChineseAttackType(AttackType);

    [JsonIgnore]
    public double ProWinRate => ProPick > 0 ? ProWin * 100.0 / ProPick : 0;

    [JsonIgnore]
    public string ProWinRateText => ProPick > 0 ? $"{ProWinRate:F1}%" : "—";

    [JsonIgnore]
    public double PubWinRate => PubPick > 0 ? PubWin * 100.0 / PubPick : 0;

    [JsonIgnore]
    public string PubWinRateText => PubPick > 0 ? $"{PubWinRate:F1}%" : "—";

    [JsonIgnore]
    public double TurboWinRate => TurboPicks > 0 ? TurboWins * 100.0 / TurboPicks : 0;

    [JsonIgnore]
    public string TurboWinRateText => TurboPicks > 0 ? $"{TurboWinRate:F1}%" : "—";

    [JsonIgnore]
    public string BaseDamageText
    {
        get
        {
            var bonus = PrimaryDamageBonus;
            return $"{BaseAttackMin + bonus}-{BaseAttackMax + bonus}";
        }
    }

    /// <summary>1 级生命（基础 + 力量 × 22）。</summary>
    [JsonIgnore]
    public int TotalHealth => BaseHealth + BaseStr * 22;

    /// <summary>1 级魔法（基础 + 智力 × 12）。</summary>
    [JsonIgnore]
    public int TotalMana => BaseMana + BaseInt * 12;

    [JsonIgnore]
    public double TotalHealthRegen => BaseHealthRegen + BaseStr * 0.1;

    [JsonIgnore]
    public double TotalManaRegen => BaseManaRegen + BaseInt * 0.05;

    [JsonIgnore]
    public double TotalArmor => BaseArmor + BaseAgi / 6.0;

    [JsonIgnore]
    public string TotalArmorText => $"{TotalArmor:F1}";

    [JsonIgnore]
    public string HealthBarText => $"{TotalHealth}  +{TotalHealthRegen:F1}";

    [JsonIgnore]
    public string ManaBarText => $"{TotalMana}  +{TotalManaRegen:F1}";

    [JsonIgnore]
    public string VisionText => $"{DayVision} / {NightVision}";

    [JsonIgnore]
    public string ProjectileSpeedText => ProjectileSpeed > 0 ? ProjectileSpeed.ToString() : "—";

    [JsonIgnore]
    public string TurnRateText => TurnRate > 0 ? TurnRate.ToString("0.##") : "—";

    [JsonIgnore]
    public string StrText => $"{BaseStr} +{StrGain:F1}";

    [JsonIgnore]
    public string AgiText => $"{BaseAgi} +{AgiGain:F1}";

    [JsonIgnore]
    public string IntText => $"{BaseInt} +{IntGain:F1}";

    /// <summary>国服定位九宫格（有该定位则进度满）。</summary>
    [JsonIgnore]
    public IReadOnlyList<HeroRoleStat> RoleStats
    {
        get
        {
            var set = new HashSet<string>(Roles, StringComparer.Ordinal);
            return HeroDisplayHelper.StandardRoles
                .Select(name => new HeroRoleStat
                {
                    Name = name,
                    Score = set.Contains(name) ? 100 : 12
                })
                .ToList();
        }
    }

    private int PrimaryDamageBonus
    {
        get
        {
            if (IsPrimaryUni)
            {
                return (int)Math.Round((BaseStr + BaseAgi + BaseInt) * 0.7);
            }

            if (IsPrimaryStr)
            {
                return BaseStr;
            }

            if (IsPrimaryAgi)
            {
                return BaseAgi;
            }

            if (IsPrimaryInt)
            {
                return BaseInt;
            }

            return 0;
        }
    }

    public override string ToString() => DisplayName;
}

/// <summary>
/// 段位选取 / 胜率。
/// </summary>
public sealed class HeroBracketStat
{
    public string Name { get; set; } = string.Empty;

    public long Picks { get; set; }

    public long Wins { get; set; }

    public double WinRate => Picks > 0 ? Wins * 100.0 / Picks : 0;

    public string WinRateText => Picks > 0 ? $"{WinRate:F1}%" : "—";

    public string PicksText => Picks > 0 ? Picks.ToString("N0") : "—";
}

/// <summary>
/// 国服详情定位条。
/// </summary>
public sealed class HeroRoleStat
{
    public string Name { get; set; } = string.Empty;

    /// <summary>0–100，用于进度条。</summary>
    public double Score { get; set; }
}

