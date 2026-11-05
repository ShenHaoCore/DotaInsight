using DotaInsight.Helpers;
using DotaInsight.Services;

namespace DotaInsight.Tests;

/// <summary>
/// 资料页头像框提取：框标记 + economy 图地址的识别、协议相对地址补全、
/// 以及无框 / 错误页 / 相邻其他图片不被误认的兜底。
/// </summary>
public class SteamAvatarFrameParserTests
{
    [Fact]
    public void Extracts_Url_WhenFrameEquipped()
    {
        const string html = """
            <div class="profile_avatar_holder">
                <div class="profile_avatar_frame">
                    <img src="https://community.akamai.steamstatic.com/economy/image/S7cBx9kbx/330x330">
                </div>
                <img class="profile_avatar_size" src="https://avatars.steamstatic.com/fef49e7fa7e1997310d705b2a6158ff8dc1cdfeb_full.jpg">
            </div>
            """;

        var url = SteamAvatarFrameParser.ExtractFrameUrl(html);

        Assert.Equal("https://community.akamai.steamstatic.com/economy/image/S7cBx9kbx/330x330", url);
    }

    [Fact]
    public void Normalizes_ProtocolRelative_Url()
    {
        const string html = """
            <div class="profile_avatar_frame"><img src="//community.akamai.steamstatic.com/economy/image/AbC42"></div>
            """;

        var url = SteamAvatarFrameParser.ExtractFrameUrl(html);

        Assert.Equal("https://community.akamai.steamstatic.com/economy/image/AbC42", url);
    }

    [Fact]
    public void Returns_Null_WhenNoFrame()
    {
        const string html = """
            <div class="profile_avatar_holder">
                <img class="profile_avatar_size" src="https://avatars.steamstatic.com/fef49e7fa7e1997310d705b2a6158ff8dc1cdfeb_full.jpg">
            </div>
            """;

        Assert.Null(SteamAvatarFrameParser.ExtractFrameUrl(html));
    }

    [Fact]
    public void Returns_Null_WhenMarkerHasNoImage()
    {
        const string html = """<div class="profile_avatar_frame"></div>""";

        Assert.Null(SteamAvatarFrameParser.ExtractFrameUrl(html));
    }

    [Fact]
    public void Returns_Null_WhenNextImageIsNotEconomy()
    {
        // 标记后面出现的不是 economy 图（例如解析落在页脚脚本附近）：宁可无框也不误认
        const string html = """
            <div class="profile_avatar_frame"></div>
            <img src="https://avatars.steamstatic.com/fef49e7fa7e1997310d705b2a6158ff8dc1cdfeb_full.jpg">
            """;

        Assert.Null(SteamAvatarFrameParser.ExtractFrameUrl(html));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Returns_Null_ForEmptyHtml(string? html)
    {
        Assert.Null(SteamAvatarFrameParser.ExtractFrameUrl(html));
    }

    /// <summary>真实抓包特征：完全私有的资料页只有 profile_private_info 占位、没有头像区（match 86745912 的 2026-11 实测）。</summary>
    private const string PrivateProfileHtml = """
        <title>Steam Community :: 76561198047011640</title>
        <div class="responsive_page_frame with_header">
          <div class="profile_private_info">
            <b>此用户档案是私密的</b>
          </div>
        </div>
        """;

    [Fact]
    public void Classify_PrivateProfile_Recognized()
    {
        Assert.Equal(
            SteamAvatarFrameService.SteamProfileHtmlKind.PrivateProfile,
            SteamAvatarFrameService.ClassifyProfileHtml(PrivateProfileHtml));
    }

    [Fact]
    public void Classify_PublicProfile_Recognized()
    {
        const string html = """<div class="profile_avatar_frame"><img src="https://community.akamai.steamstatic.com/economy/image/x"></div>""";
        Assert.Equal(
            SteamAvatarFrameService.SteamProfileHtmlKind.Profile,
            SteamAvatarFrameService.ClassifyProfileHtml(html));
    }

    [Fact]
    public void Classify_ErrorPage_NotProfile()
    {
        const string html = """<title>Sorry! We've had to throttle this page</title>""";
        Assert.Equal(
            SteamAvatarFrameService.SteamProfileHtmlKind.NotProfile,
            SteamAvatarFrameService.ClassifyProfileHtml(html));
    }
}
