using System.Text.Json.Serialization;
using System.Windows.Media;
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

    // heroStats 各分段原始场次：仅用于接收接口数据，归一化生成 Brackets 后清零，
    // WhenWritingDefault 保证为 0 时不写入 LiteDB 缓存。
    [JsonPropertyName("1_pick")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public long Pick1 { get; set; }
    [JsonPropertyName("2_pick")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public long Pick2 { get; set; }
    [JsonPropertyName("3_pick")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public long Pick3 { get; set; }
    [JsonPropertyName("4_pick")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public long Pick4 { get; set; }
    [JsonPropertyName("5_pick")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public long Pick5 { get; set; }
    [JsonPropertyName("6_pick")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public long Pick6 { get; set; }
    [JsonPropertyName("7_pick")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public long Pick7 { get; set; }
    [JsonPropertyName("8_pick")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public long Pick8 { get; set; }

    [JsonPropertyName("1_win")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public long Win1 { get; set; }
    [JsonPropertyName("2_win")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public long Win2 { get; set; }
    [JsonPropertyName("3_win")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public long Win3 { get; set; }
    [JsonPropertyName("4_win")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public long Win4 { get; set; }
    [JsonPropertyName("5_win")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public long Win5 { get; set; }
    [JsonPropertyName("6_win")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public long Win6 { get; set; }
    [JsonPropertyName("7_win")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public long Win7 { get; set; }
    [JsonPropertyName("8_win")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public long Win8 { get; set; }

    [JsonPropertyName("brackets")]
    public List<HeroBracketStat> Brackets { get; set; } = [];

    /// <summary>全分段选取场次（由各段位聚合或职业数据）。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Matches { get; set; }

    /// <summary>胜率 0–100。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double WinRate { get; set; }

    /// <summary>选取率 0–100（相对全体英雄选取总和）。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double PickRate { get; set; }

    // —— 派生值惰性缓存 ——
    // HeroStat 无属性通知，反序列化/归一化定型后派生值不再变化，
    // 而 127 磁贴 / 40 行对位 / 8 行分段会随绑定反复求值这些 getter，缓存为字段避免重复分配。
    // 注意：写入 LocalizedName 等基础字段前不要先读取派生属性（服务的加载顺序已满足）。
    private string? _displayName;
    private string? _iconUrl;
    private string? _smallIconUrl;
    private string? _englishName;
    private string? _winRateText;
    private string? _pickRateText;
    private string? _attackTypeText;
    private IReadOnlyList<string>? _chineseRoles;
    private IReadOnlyList<HeroRoleStat>? _roleStats;

    [JsonIgnore]
    public string IconUrl => _iconUrl ??= HeroAssetHelper.GetIconUrl(Name);

    [JsonIgnore]
    public string SmallIconUrl => _smallIconUrl ??= HeroAssetHelper.GetSmallIconUrl(Name);

    [JsonIgnore]
    public ImageSource PrimaryAttrIcon => HeroAssetHelper.GetPrimaryAttrIcon(PrimaryAttr);

    [JsonIgnore]
    public ImageSource StrIcon => HeroAssetHelper.StrIcon;

    [JsonIgnore]
    public ImageSource AgiIcon => HeroAssetHelper.AgiIcon;

    [JsonIgnore]
    public ImageSource IntIcon => HeroAssetHelper.IntIcon;

    [JsonIgnore]
    public ImageSource ArmorIcon => HeroAssetHelper.ArmorIcon;

    [JsonIgnore]
    public ImageSource MagicResistIcon => HeroAssetHelper.MagicResistIcon;

    [JsonIgnore]
    public ImageSource DamageIcon => HeroAssetHelper.DamageIcon;

    [JsonIgnore]
    public ImageSource AttackTimeIcon => HeroAssetHelper.AttackTimeIcon;

    [JsonIgnore]
    public ImageSource AttackRangeIcon => HeroAssetHelper.AttackRangeIcon;

    [JsonIgnore]
    public ImageSource MoveSpeedIcon => HeroAssetHelper.MoveSpeedIcon;

    [JsonIgnore]
    public ImageSource VisionIcon => HeroAssetHelper.VisionIcon;

    [JsonIgnore]
    public string DisplayName => _displayName ??= string.IsNullOrWhiteSpace(LocalizedName) ? Name : LocalizedName;

    /// <summary>英文展示名，如 Elder Titan。</summary>
    [JsonIgnore]
    public string EnglishName => _englishName ??= HeroAssetHelper.GetEnglishDisplayName(Name);

    [JsonIgnore]
    public bool IsPrimaryStr => PrimaryAttr is "力量" or "str";

    [JsonIgnore]
    public bool IsPrimaryAgi => PrimaryAttr is "敏捷" or "agi";

    [JsonIgnore]
    public bool IsPrimaryInt => PrimaryAttr is "智力" or "int";

    [JsonIgnore]
    public bool IsPrimaryUni => PrimaryAttr is "全才" or "all" or "universal";

    [JsonIgnore]
    public string WinRateText => _winRateText ??= $"{WinRate:F1}%";

    [JsonIgnore]
    public string PickRateText => _pickRateText ??= $"{PickRate:F1}%";

    [JsonIgnore]
    public IReadOnlyList<string> ChineseRoles => _chineseRoles ??= HeroDisplayHelper.ToChineseRoles(Roles);

    [JsonIgnore]
    public bool HasRoles => Roles.Count > 0;

    [JsonIgnore]
    public string AttackTypeText => _attackTypeText ??= HeroDisplayHelper.ToChineseAttackType(AttackType);

    [JsonIgnore]
    public bool IsMelee => AttackType is "Melee" or "近战";

    [JsonIgnore]
    public bool IsRanged => AttackType is "Ranged" or "远程";

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
    public string MagicResistText => $"{BaseMagicResist:0.##}%";

    [JsonIgnore]
    public string AttackRateText => AttackRate > 0 ? AttackRate.ToString("0.##") : "—";

    [JsonIgnore]
    public string StrText => $"{BaseStr} +{StrGain:F1}";

    [JsonIgnore]
    public string AgiText => $"{BaseAgi} +{AgiGain:F1}";

    [JsonIgnore]
    public string IntText => $"{BaseInt} +{IntGain:F1}";

    /// <summary>
    /// 国服定位九宫格：按官网 role_levels 0–3 级渲染（33% / 67% / 100%）；
    /// 新英雄不在等级表中时回退为“有该定位则满格”。
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<HeroRoleStat> RoleStats => _roleStats ??= BuildRoleStats();

    private IReadOnlyList<HeroRoleStat> BuildRoleStats()
    {
        var set = new HashSet<string>(ChineseRoles, StringComparer.Ordinal);
        return HeroDisplayHelper.StandardRoles
            .Select((name, i) => new HeroRoleStat
            {
                Name = name,
                Score = HeroRoleLevelTable.TryGetLevel(Id, i, out var level)
                    ? level * 100d / 3d
                    : (set.Contains(name) ? 100 : 0)
            })
            .ToList();
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
    public string Name { get; init; } = string.Empty;

    public long Picks { get; init; }

    public long Wins { get; init; }

    public double WinRate => Picks > 0 ? Wins * 100.0 / Picks : 0;

    public string WinRateText => Picks > 0 ? $"{WinRate:F1}%" : "—";

    public string PicksText => Picks > 0 ? Picks.ToString("N0") : "—";
}

/// <summary>
/// 国服详情定位条。
/// </summary>
public sealed class HeroRoleStat
{
    public string Name { get; init; } = string.Empty;

    /// <summary>0–100，用于进度条。</summary>
    public double Score { get; init; }
}

