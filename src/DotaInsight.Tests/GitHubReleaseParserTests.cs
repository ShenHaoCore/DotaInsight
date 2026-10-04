using DotaInsight.Helpers;

namespace DotaInsight.Tests;

/// <summary>
/// 升级信息解析：GitHub Releases API JSON → AutoUpdater 要的版本 / 下载地址。
/// 这是升级链路里唯一容易出错又完全可离线验证的一段（网络与解压环节没法单测）。
/// </summary>
public class GitHubReleaseParserTests
{
    private const string SampleJson = """
        {
          "tag_name": "v0.2.0",
          "name": "DotaInsight v0.2.0",
          "draft": false,
          "prerelease": false,
          "html_url": "https://github.com/ShenHaoCore/DotaInsight/releases/tag/v0.2.0",
          "body": "## 更新内容\n- 新增比赛详情页",
          "assets": [
            {
              "name": "checksums.txt",
              "browser_download_url": "https://github.com/ShenHaoCore/DotaInsight/releases/download/v0.2.0/checksums.txt"
            },
            {
              "name": "DotaInsight-v0.2.0-win-x64.zip",
              "browser_download_url": "https://github.com/ShenHaoCore/DotaInsight/releases/download/v0.2.0/DotaInsight-v0.2.0-win-x64.zip"
            }
          ]
        }
        """;

    [Fact]
    public void TryParse_NormalRelease_ReadsVersionAndZipAsset()
    {
        var ok = GitHubReleaseParser.TryParse(SampleJson, out var info, out var error);

        Assert.True(ok, error);
        Assert.NotNull(info);
        Assert.Equal(new Version(0, 2, 0, 0), info!.Version);
        Assert.Equal("v0.2.0", info.TagName);
        Assert.Equal("v0.2.0", info.VersionText);
        Assert.EndsWith("DotaInsight-v0.2.0-win-x64.zip", info.DownloadUrl);
        Assert.EndsWith("/tag/v0.2.0", info.ChangelogUrl);
        Assert.Contains("比赛详情页", info.Notes);
    }

    [Fact]
    public void TryParse_AssetWithoutTagInName_StillMatchedBySuffix()
    {
        const string json = """
            {
              "tag_name": "v1.3.0",
              "html_url": "https://example.com/tag",
              "assets": [
                { "name": "DotaInsight-win-x64.zip", "browser_download_url": "https://example.com/a.zip" }
              ]
            }
            """;

        Assert.True(GitHubReleaseParser.TryParse(json, out var info, out var error), error);
        Assert.Equal("https://example.com/a.zip", info!.DownloadUrl);
    }

    /// <summary>
    /// 真实仓库常同时挂着别的架构 / 用途的 zip（实测 ripgrep 的资产里 aarch64 包排在前面）。
    /// 宁可不升级，也不能把 aarch64 包装进 x64 程序里。
    /// </summary>
    [Fact]
    public void TryParse_OnlyOtherPlatformZip_Fails()
    {
        const string json = """
            {
              "tag_name": "v15.2.0",
              "assets": [
                { "name": "ripgrep-15.2.0-aarch64-pc-windows-msvc.zip", "browser_download_url": "https://example.com/arm.zip" },
                { "name": "ripgrep-15.2.0-x86_64-pc-windows-msvc.zip", "browser_download_url": "https://example.com/x64.zip" },
                { "name": "GroupPolicyObjectFiles-1.0.zip", "browser_download_url": "https://example.com/gpo.zip" }
              ]
            }
            """;

        Assert.False(GitHubReleaseParser.TryParse(json, out var info, out var error));
        Assert.Null(info);
        Assert.Contains("没有提供", error);
    }

    /// <summary>命名里用点号代替连字符时也能认出来。</summary>
    [Fact]
    public void TryParse_WinX64ZipWithDifferentSeparator_IsAccepted()
    {
        const string json = """
            {
              "tag_name": "v1.3.0",
              "assets": [
                { "name": "DotaInsight.win-x64.zip", "browser_download_url": "https://example.com/a.zip" }
              ]
            }
            """;

        Assert.True(GitHubReleaseParser.TryParse(json, out var info, out var error), error);
        Assert.Equal("https://example.com/a.zip", info!.DownloadUrl);
    }

    [Fact]
    public void TryParse_NoZipAsset_Fails()
    {
        const string json = """
            {
              "tag_name": "v1.3.0",
              "assets": [
                { "name": "DotaInsight.exe", "browser_download_url": "https://example.com/a.exe" }
              ]
            }
            """;

        Assert.False(GitHubReleaseParser.TryParse(json, out var info, out var error));
        Assert.Null(info);
        Assert.Contains("没有提供", error);
    }

    [Fact]
    public void TryParse_Draft_Fails()
    {
        const string json = """
            { "tag_name": "v9.9.9", "draft": true, "assets": [] }
            """;

        Assert.False(GitHubReleaseParser.TryParse(json, out _, out var error));
        Assert.Contains("草稿", error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ not json")]
    [InlineData("[1,2,3]")]
    [InlineData("""{ "assets": [] }""")]
    [InlineData("""{ "tag_name": "release-one", "assets": [] }""")]
    public void TryParse_InvalidPayload_Fails(string json)
    {
        Assert.False(GitHubReleaseParser.TryParse(json, out var info, out var error));
        Assert.Null(info);
        Assert.NotEqual(string.Empty, error);
    }

    [Theory]
    [InlineData("v1.2.3", "1.2.3.0")]
    [InlineData("1.2.3", "1.2.3.0")]
    [InlineData("V2.0", "2.0.0.0")]
    [InlineData("v0.1.0.4", "0.1.0.4")]
    public void TryParseVersion_NormalizesToFourParts(string tag, string expected)
    {
        Assert.True(GitHubReleaseParser.TryParseVersion(tag, out var version));
        Assert.Equal(new Version(expected), version);
    }

    /// <summary>
    /// 四段补齐的意义：否则 0.2.0（Build=-1）与已装的 0.2.0.0 比较会判定为「更小」，
    /// 刚发布的新版本反而检测不到。
    /// </summary>
    [Fact]
    public void VersionComparison_SameRelease_IsNotAnUpdate()
    {
        GitHubReleaseParser.TryParseVersion("v0.2.0", out var remote);
        var installed = new Version(0, 2, 0, 0);

        Assert.False(remote > installed);
    }

    [Theory]
    [InlineData("v0.2.0", "0.1.0.0", true)]
    [InlineData("v0.1.1", "0.1.0.0", true)]
    [InlineData("v0.1.0", "0.1.0.0", false)]
    [InlineData("v0.0.9", "0.1.0.0", false)]
    [InlineData("v1.0.0", "0.1.0.0", true)]
    public void VersionComparison_DecidesUpdateAvailability(string tag, string installedText, bool expected)
    {
        Assert.True(GitHubReleaseParser.TryParseVersion(tag, out var remote));
        Assert.Equal(expected, remote > new Version(installedText));
    }
}
