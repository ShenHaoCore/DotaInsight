namespace DotaInsight.Models;

/// <summary>
/// 国服英雄资料（背景、技能、webm 等）。由资料服务映射 / 缓存反序列化产生，之后不可变。
/// </summary>
public sealed class HeroDetailProfile
{
    public int HeroId { get; init; }

    public string InternalName { get; init; } = string.Empty;

    /// <summary>短介绍（去 HTML）。</summary>
    public string Hype { get; init; } = string.Empty;

    /// <summary>完整背景故事（去 HTML）。</summary>
    public string Bio { get; init; } = string.Empty;

    /// <summary>一句话定位。</summary>
    public string NpeDesc { get; init; } = string.Empty;

    public string VideoUrl { get; init; } = string.Empty;

    public string PosterUrl { get; init; } = string.Empty;

    public int Complexity { get; init; }

    /// <summary>九宫格定位等级 0–3，顺序同 <see cref="Helpers.HeroDisplayHelper.StandardRoles"/>。</summary>
    public List<int> RoleLevels { get; init; } = [];

    public List<HeroAbilityInfo> Abilities { get; init; } = [];

    /// <summary>天赋树，自上而下列出 25/20/15/10 级行。</summary>
    public List<HeroTalentRow> Talents { get; init; } = [];
}

/// <summary>
/// 英雄技能摘要。
/// </summary>
public sealed class HeroAbilityInfo
{
    public int Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public string Lore { get; init; } = string.Empty;

    public string IconUrl { get; init; } = string.Empty;

    public bool IsInnate { get; init; }

    /// <summary>被动技能（无冷却无耗蓝时界面显示"被动"）。</summary>
    public bool IsPassive { get; init; }

    public bool HasScepter { get; init; }

    public bool HasShard { get; init; }

    /// <summary>技能本身由阿哈利姆神杖/魔晶提供。</summary>
    public bool GrantedByScepter { get; init; }

    public bool GrantedByShard { get; init; }

    /// <summary>冷却时间（多级用 / 连接），全 0 表示无冷却，留空。</summary>
    public string CooldownText { get; init; } = string.Empty;

    /// <summary>魔法消耗（多级用 / 连接），全 0 时留空。</summary>
    public string ManaCostText { get; init; } = string.Empty;

    /// <summary>技能备注条目（notes_loc）。</summary>
    public List<string> Notes { get; init; } = [];

    /// <summary>阿哈利姆神杖升级说明。</summary>
    public string ScepterDescription { get; init; } = string.Empty;

    /// <summary>阿哈利姆魔晶升级说明。</summary>
    public string ShardDescription { get; init; } = string.Empty;

    public bool HasCooldown => !string.IsNullOrEmpty(CooldownText);

    public bool HasManaCost => !string.IsNullOrEmpty(ManaCostText);

    public bool HasNotes => Notes.Count > 0;

    public bool HasScepterDescription => !string.IsNullOrWhiteSpace(ScepterDescription);

    public bool HasShardDescription => !string.IsNullOrWhiteSpace(ShardDescription);
}

/// <summary>
/// 天赋树的一行：同一等级左右二选一。
/// </summary>
public sealed class HeroTalentRow
{
    public int Level { get; init; }

    public string Left { get; init; } = string.Empty;

    public string Right { get; init; } = string.Empty;

    public bool HasLeft => !string.IsNullOrWhiteSpace(Left);

    public bool HasRight => !string.IsNullOrWhiteSpace(Right);
}
