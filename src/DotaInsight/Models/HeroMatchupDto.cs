using System.Text.Json.Serialization;

namespace DotaInsight.Models;

/// <summary>
/// OpenDota /api/heroes/{id}/matchups 单条对位数据。
/// wins / games_played 表示「己方英雄」对阵该敌方英雄的战绩。
/// </summary>
public sealed class HeroMatchupDto
{
    [JsonPropertyName("hero_id")]
    public int HeroId { get; set; }

    [JsonPropertyName("games_played")]
    public int GamesPlayed { get; set; }

    [JsonPropertyName("wins")]
    public int Wins { get; set; }
}
