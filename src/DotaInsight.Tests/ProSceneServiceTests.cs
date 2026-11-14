using DotaInsight.Models;
using DotaInsight.Services;

namespace DotaInsight.Tests;

public class ProSceneServiceTests
{
    private static ProPlayerApiDto Pro(long id, string? name = null, string? persona = null,
        int? role = null, long teamId = 0, string? teamName = null, string? avatar = null)
        => new()
        {
            AccountId = id,
            Name = name,
            PersonaName = persona,
            FantasyRole = role,
            TeamId = teamId,
            TeamName = teamName,
            AvatarFull = avatar,
            IsPro = true
        };

    private static ProTeamPlayerApiDto Roster(long id, string? name, int games, int wins, bool current = true)
        => new() { AccountId = id, Name = name, GamesPlayed = games, Wins = wins, IsCurrentTeamMember = current };

    [Fact]
    public void BuildPlayerRows_KeepsOnlyCurrentMembersWithGames()
    {
        var pros = new List<ProPlayerApiDto> { Pro(1, "Yatoro", "yator0", role: 1, teamId: 10) };
        var blocks = new List<(long, ProTeamApiDto, List<ProTeamPlayerApiDto>)>
        {
            (10, new ProTeamApiDto { TeamId = 10, Name = "Team Spirit", Wins = 5, Losses = 2 },
                [Roster(1, "Yatoro", 120, 80), Roster(2, "替补", 10, 5, current: false), Roster(3, "零场次", 0, 0)])
        };

        var rows = ProSceneService.BuildPlayerRows(pros, blocks);

        Assert.Single(rows);
        Assert.Equal("Yatoro", rows[0].DisplayName);
        Assert.Equal("Team Spirit", rows[0].TeamName);
        Assert.Equal("一号位", rows[0].RoleText);
        Assert.Equal(120, rows[0].GamesPlayed);
        Assert.Equal(80, rows[0].Wins);
        Assert.Equal(40, rows[0].Losses);
        Assert.Equal(1, rows[0].RankNumber);
    }

    [Fact]
    public void BuildPlayerRows_DeduplicatesAccountsKeepingFirstBlock()
    {
        var pros = new List<ProPlayerApiDto> { Pro(1) };
        var blocks = new List<(long, ProTeamApiDto, List<ProTeamPlayerApiDto>)>
        {
            (10, new ProTeamApiDto { TeamId = 10, Name = "A队" }, [Roster(1, "同一个人", 100, 60)]),
            (20, new ProTeamApiDto { TeamId = 20, Name = "B队" }, [Roster(1, "同一个人", 40, 20)])
        };

        var rows = ProSceneService.BuildPlayerRows(pros, blocks);

        // 同一账号只保留出场更多的队（B 队块按队内最高场次排在 A 队后）
        Assert.Single(rows);
        Assert.Equal("A队", rows[0].TeamName);
        Assert.Equal(100, rows[0].GamesPlayed);
    }

    [Fact]
    public void BuildPlayerRows_FallsBackToPersonaNameWhenRosterNameMissing()
    {
        var pros = new List<ProPlayerApiDto> { Pro(1, persona: "昵称") };
        var blocks = new List<(long, ProTeamApiDto, List<ProTeamPlayerApiDto>)>
        {
            (10, new ProTeamApiDto { TeamId = 10, Name = "A队" }, [Roster(1, name: null, 50, 25)])
        };

        var rows = ProSceneService.BuildPlayerRows(pros, blocks);

        Assert.Equal("昵称", rows[0].DisplayName);
    }

    [Fact]
    public void BuildTeamRows_SortsByRatingDescendingAndNumbers()
    {
        var blocks = new List<(long, ProTeamApiDto, List<ProTeamPlayerApiDto>)>
        {
            (1, new ProTeamApiDto { TeamId = 1, Name = "低分队", Rating = 500.4, Wins = 3, Losses = 7 }, []),
            (2, new ProTeamApiDto { TeamId = 2, Name = "高分队", Tag = "HI", Rating = 1200.6, Wins = 9, Losses = 1 }, []),
            (3, new ProTeamApiDto { TeamId = 3, Name = "无战绩队", Rating = 800, Wins = 0, Losses = 0 }, [])
        };

        var rows = ProSceneService.BuildTeamRows(blocks);

        Assert.Equal(2, rows.Count);
        Assert.Equal("高分队", rows[0].Name);
        Assert.Equal("1201", rows[0].RatingText);
        Assert.Equal(1, rows[0].RankNumber);
        Assert.Equal(90.0, rows[0].WinRate);
        Assert.Equal("90.0%", rows[0].WinRateText);
        Assert.Equal("低分队", rows[1].Name);
        Assert.Equal(2, rows[1].RankNumber);
    }

    [Fact]
    public void RoleText_MapsFantasyRoles()
    {
        Assert.Equal("中单", ProSceneService.RoleText(2));
        Assert.Equal("五号位", ProSceneService.RoleText(5));
        Assert.Equal(string.Empty, ProSceneService.RoleText(0));
        Assert.Equal(string.Empty, ProSceneService.RoleText(null));
    }
}
