using System.Text.Json.Serialization;

namespace DotaInsight.Models;

/// <summary>
/// 国服英雄资料（背景、技能、webm 等）。
/// </summary>
public sealed class HeroDetailProfile
{
    public int HeroId { get; set; }

    public string InternalName { get; set; } = string.Empty;

    /// <summary>短介绍（去 HTML）。</summary>
    public string Hype { get; set; } = string.Empty;

    /// <summary>完整背景故事（去 HTML）。</summary>
    public string Bio { get; set; } = string.Empty;

    /// <summary>一句话定位。</summary>
    public string NpeDesc { get; set; } = string.Empty;

    public string VideoUrl { get; set; } = string.Empty;

    public string PosterUrl { get; set; } = string.Empty;

    public int Complexity { get; set; }

    /// <summary>九宫格定位等级 0–3，顺序同 <see cref="Helpers.HeroDisplayHelper.StandardRoles"/>。</summary>
    public List<int> RoleLevels { get; set; } = [];

    public List<HeroAbilityInfo> Abilities { get; set; } = [];
}

/// <summary>
/// 英雄技能摘要。
/// </summary>
public sealed class HeroAbilityInfo
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Lore { get; set; } = string.Empty;

    public string IconUrl { get; set; } = string.Empty;

    public bool IsInnate { get; set; }

    public bool HasScepter { get; set; }

    public bool HasShard { get; set; }
}
