using System.Text.Json.Serialization;

namespace DotaInsight.Models;

/// <summary>OpenDota proPlayers 元素。</summary>
internal sealed class ProPlayerApiDto
{
    [JsonPropertyName("account_id")]
    public long AccountId { get; set; }

    [JsonPropertyName("personaname")]
    public string? PersonaName { get; set; }

    /// <summary>认证选手名（非空即职业选手）。</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("avatarfull")]
    public string? AvatarFull { get; set; }

    /// <summary>位置：1-5 号位，0/空为教练或未知。</summary>
    [JsonPropertyName("fantasy_role")]
    public int? FantasyRole { get; set; }

    [JsonPropertyName("team_id")]
    public long TeamId { get; set; }

    [JsonPropertyName("team_name")]
    public string? TeamName { get; set; }

    [JsonPropertyName("country_code")]
    public string? CountryCode { get; set; }

    [JsonPropertyName("is_pro")]
    public bool? IsPro { get; set; }
}

/// <summary>OpenDota teams/{id} 战队资料。</summary>
internal sealed class ProTeamApiDto
{
    [JsonPropertyName("team_id")]
    public long TeamId { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("tag")]
    public string? Tag { get; set; }

    [JsonPropertyName("rating")]
    public double? Rating { get; set; }

    [JsonPropertyName("wins")]
    public int Wins { get; set; }

    [JsonPropertyName("losses")]
    public int Losses { get; set; }

    [JsonPropertyName("last_match_time")]
    public long? LastMatchTime { get; set; }
}

/// <summary>OpenDota teams/{id}/players 元素。</summary>
internal sealed class ProTeamPlayerApiDto
{
    [JsonPropertyName("account_id")]
    public long AccountId { get; set; }

    /// <summary>选手认证名。</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("games_played")]
    public int GamesPlayed { get; set; }

    [JsonPropertyName("wins")]
    public int Wins { get; set; }

    [JsonPropertyName("is_current_team_member")]
    public bool? IsCurrentTeamMember { get; set; }
}

/// <summary>职业选手榜单行：某选手在其现役战队的出场/胜负聚合。</summary>
public sealed class ProPlayerRow
{
    /// <summary>榜单名次（聚合时定序）。</summary>
    public int RankNumber { get; set; }

    public long AccountId { get; init; }

    /// <summary>认证选手名（proPlayers.name，缺省回退昵称）。</summary>
    public string DisplayName { get; init; } = string.Empty;

    public string AvatarUrl { get; init; } = string.Empty;

    public string TeamName { get; init; } = string.Empty;

    /// <summary>位置文案（一号位/中单/…），未知为空串。</summary>
    public string RoleText { get; init; } = string.Empty;

    public int GamesPlayed { get; init; }

    public int Wins { get; init; }

    public int Losses => GamesPlayed - Wins;

    public string WinRateText { get; init; } = string.Empty;

    /// <summary>胜率数值（0-1），进度条与排序用。</summary>
    public double WinRate { get; init; }
}

/// <summary>职业战队榜单行。</summary>
public sealed class ProTeamRow
{
    /// <summary>榜单名次（聚合时定序）。</summary>
    public int RankNumber { get; set; }

    public long TeamId { get; init; }

    public string Name { get; init; } = string.Empty;

    public string Tag { get; init; } = string.Empty;

    public string RatingText { get; init; } = string.Empty;

    /// <summary>评分数值，排序用。</summary>
    public double Rating { get; init; }

    public int Wins { get; init; }

    public int Losses { get; init; }

    public string WinRateText { get; init; } = string.Empty;

    public double WinRate { get; init; }
}

/// <summary>职业选手榜单聚合结果（缓存实体）。</summary>
public sealed class ProSceneData
{
    public List<ProPlayerRow> PlayerRows { get; set; } = [];

    public List<ProTeamRow> TeamRows { get; set; } = [];

    /// <summary>聚合时间（UTC），页面提示数据新鲜度。</summary>
    public DateTime AggregatedUtc { get; set; }
}
