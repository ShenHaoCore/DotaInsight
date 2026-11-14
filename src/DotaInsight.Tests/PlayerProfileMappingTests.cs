using System.Text.Json;
using DotaInsight.Models;

namespace DotaInsight.Tests;

/// <summary>
/// 玩家资料映射：OpenDota /players/{id} 的 computed_mmr（隐藏分估算）与 rank_tier。
/// computed_mmr 冠绝玩家为 null；旧字段 mmr_estimate 已被 OpenDota 移除。
/// </summary>
public class PlayerProfileMappingTests
{
    private const string FixtureJson = """
        {
          "profile": {
            "account_id": 136515251,
            "personaname": "我刀呢?",
            "avatarfull": "https://avatars.steamstatic.com/x.jpg",
            "name": null
          },
          "rank_tier": 42,
          "leaderboard_rank": null,
          "computed_mmr": 4001.25,
          "computed_mmr_turbo": 3825.43
        }
        """;

    private const string ImmortalFixtureJson = """
        {
          "profile": {
            "account_id": 173978074,
            "personaname": "NothingToSay",
            "avatarfull": "https://avatars.steamstatic.com/y.jpg",
            "name": "NothingToSay"
          },
          "rank_tier": 80,
          "leaderboard_rank": 12,
          "computed_mmr": null,
          "computed_mmr_turbo": null
        }
        """;

    [Fact]
    public void ComputedMmr_MapsEstimateAndFormats()
    {
        var dto = JsonSerializer.Deserialize<PlayerApiDto>(FixtureJson, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = false
        });

        Assert.NotNull(dto);
        Assert.Equal(42, dto!.RankTier);
        Assert.Equal(4001.25, dto.ComputedMmr);
        Assert.Equal(3825.43, dto.ComputedMmrTurbo);

        var profile = new PlayerProfile
        {
            AccountId = dto.Profile!.AccountId,
            PersonaName = dto.Profile.PersonaName ?? string.Empty,
            RankTier = dto.RankTier,
            LeaderboardRank = dto.LeaderboardRank,
            ProName = dto.Profile.Name,
            ComputedMmr = dto.ComputedMmr
        };

        Assert.Equal("统将 2", profile.RankText);
        Assert.Equal("~4001", profile.MmrText);
        Assert.Null(profile.LeaderboardText);
    }

    [Fact]
    public void ComputedMmr_NullForImmortal_SoMmrTextStaysNull()
    {
        var dto = JsonSerializer.Deserialize<PlayerApiDto>(ImmortalFixtureJson, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = false
        });

        Assert.NotNull(dto);
        Assert.Null(dto!.ComputedMmr);

        var profile = new PlayerProfile
        {
            AccountId = dto.Profile!.AccountId,
            PersonaName = dto.Profile.PersonaName ?? string.Empty,
            RankTier = dto.RankTier,
            LeaderboardRank = dto.LeaderboardRank,
            ProName = dto.Profile.Name,
            ComputedMmr = dto.ComputedMmr
        };

        Assert.Equal("冠绝", profile.RankText);
        Assert.Equal("天梯 #12", profile.LeaderboardText);
        Assert.Null(profile.MmrText);
    }
}
