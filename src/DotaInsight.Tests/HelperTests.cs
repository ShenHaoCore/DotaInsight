using DotaInsight.Helpers;
using DotaInsight.Models;
using System.Text.Json;

namespace DotaInsight.Tests;

public class AbilityTextHelperTests
{
    [Fact]
    public void StripHtml_ReplacesBrAndTags()
    {
        var text = AbilityTextHelper.StripHtml("一行<br/>二行<b>粗</b>");
        Assert.Equal("一行\n二行粗", text);
    }

    [Fact]
    public void FormatDescription_ReplacesTokensAndLiteralPercent()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["mana_threshold"] = "50"
        };

        var text = AbilityTextHelper.FormatDescription(
            "高于%mana_threshold%%%魔法时无效。",
            values);

        Assert.Equal("高于50%魔法时无效。", text);
    }

    [Fact]
    public void FormatSpecialValue_JoinsLevels()
    {
        Assert.Equal("25/30/35/40", AbilityTextHelper.FormatSpecialValue([25, 30, 35, 40]));
        Assert.Equal("1.8/2.7", AbilityTextHelper.FormatSpecialValue([1.8, 2.7]));
    }
}

public class CacheFileNamingTests
{
    [Fact]
    public void FromUrl_DifferentPaths_DoNotCollide()
    {
        var a = CacheFileNaming.FromUrl(
            "https://cdn.example.com/apps/dota2/images/dota_react/heroes/antimage.png");
        var b = CacheFileNaming.FromUrl(
            "https://cdn.example.com/apps/dota2/images/dota_react/heroes/icons/antimage.png");
        var c = CacheFileNaming.FromUrl(
            "https://cdn.example.com/apps/dota2/videos/dota_react/heroes/renders/antimage.png");

        Assert.NotEqual(a, b);
        Assert.NotEqual(a, c);
        Assert.NotEqual(b, c);
        Assert.EndsWith(".png", a);
    }
}

public class SteamAccountIdTests
{
    [Theory]
    [InlineData("86745912", 86745912)]
    [InlineData("76561198047011640", 86745912)]
    public void TryParse_SupportsAccountAndSteamId64(string input, long expected)
    {
        Assert.True(SteamAccountId.TryParse(input, out var id));
        Assert.Equal(expected, id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0")]
    public void TryParse_RejectsInvalid(string? input)
    {
        Assert.False(SteamAccountId.TryParse(input, out _));
    }
}

public class HeroRoleTests
{
    [Fact]
    public void ToChineseRoles_NormalizesAlias_SurvivalToDurable()
    {
        // 国服数据中 Durable 有“生存 / 耐久”两种译名，需归一
        var zh = HeroDisplayHelper.ToChineseRoles(["核心", "辅助", "生存"]);

        Assert.Equal(["核心", "辅助", "耐久"], zh);
    }

    [Fact]
    public void ToChineseRoles_FromEnglish_Works()
    {
        var zh = HeroDisplayHelper.ToChineseRoles(["Carry", "Support", "Durable"]);

        Assert.Equal(["核心", "辅助", "耐久"], zh);
    }

    [Fact]
    public void RoleStats_AliasRole_FillsBar()
    {
        // Abaddon：官网等级 核心1级 / 辅助2级 / 耐久2级；“生存”别名需归一到“耐久”
        var hero = new HeroStat
        {
            Id = 102,
            Name = "npc_dota_hero_abaddon",
            LocalizedName = "亚巴顿",
            PrimaryAttr = "全才",
            Roles = ["辅助", "核心", "生存"]
        };

        var carry = hero.RoleStats.First(r => r.Name == "核心");
        Assert.Equal(100d / 3d, carry.Score, 6);

        var support = hero.RoleStats.First(r => r.Name == "辅助");
        Assert.Equal(200d / 3d, support.Score, 6);

        var durable = hero.RoleStats.First(r => r.Name == "耐久");
        Assert.Equal(200d / 3d, durable.Score, 6);

        var absent = hero.RoleStats.First(r => r.Name == "爆发");
        Assert.Equal(0, absent.Score);
    }

    [Fact]
    public void RoleStats_UnknownHero_FallsBackToBoolean()
    {
        // 新英雄不在等级表中：有定位则满格，无则空
        var hero = new HeroStat
        {
            Id = 9999,
            Name = "npc_dota_hero_newhero",
            LocalizedName = "新英雄",
            PrimaryAttr = "力量",
            Roles = ["核心", "控制"]
        };

        Assert.Equal(100, hero.RoleStats.First(r => r.Name == "核心").Score);
        Assert.Equal(100, hero.RoleStats.First(r => r.Name == "控制").Score);
        Assert.Equal(0, hero.RoleStats.First(r => r.Name == "辅助").Score);
    }
}

public class HttpCallTests
{
    [Fact]
    public void IsUserCancellation_OnlyWhenTokenCanceled()
    {
        using var cts = new CancellationTokenSource();
        var ex = new TaskCanceledException();

        Assert.False(HttpCall.IsUserCancellation(ex, cts.Token));
        cts.Cancel();
        Assert.True(HttpCall.IsUserCancellation(ex, cts.Token));
    }

    [Fact]
    public void JsonOptions_NullNumberField_ReadsAsDefault()
    {
        // 复现 OpenDota heroStats：turn_rate 等字段可能为 null
        const string json =
            "[{\"id\":1,\"turn_rate\":null,\"move_speed\":305,\"str_gain\":\"1.3\"}]";

        var heroes = JsonSerializer.Deserialize<List<HeroStat>>(json, HttpCall.JsonOptions);

        Assert.NotNull(heroes);
        Assert.Single(heroes);
        Assert.Equal(0, heroes[0].TurnRate);
        Assert.Equal(305, heroes[0].MoveSpeed);
        Assert.Equal(1.3, heroes[0].StrGain, 3);
    }

    [Fact]
    public void JsonOptions_RoundTrip_WritesNumbers()
    {
        var hero = new HeroStat { Id = 7, MoveSpeed = 290, WinRate = 52.4 };

        var json = JsonSerializer.Serialize(hero, HttpCall.JsonOptions);

        Assert.Contains("\"move_speed\":290", json);
        Assert.Contains("\"WinRate\":52.4", json);
    }
}
