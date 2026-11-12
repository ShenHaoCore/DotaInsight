namespace DotaInsight.Helpers;

/// <summary>
/// 全项目外部地址统一管理。所有 BaseAddress、CDN 前缀、页面 URL 集中在此，
/// 改域名/换源时只改这一处。
/// </summary>
public static class ApiEndpoints
{
    // ===== OpenDota API =====
    public const string OpenDotaBase = "https://api.opendota.com/";

    // ===== Steam CDN（英雄/物品/地图素材） =====
    public const string SteamCdnBase = "https://cdn.cloudflare.steamstatic.com";
    public const string DotaReactImagesBase = SteamCdnBase + "/apps/dota2/images/dota_react/";
    public const string DotaReactVideosBase = SteamCdnBase + "/apps/dota2/videos/dota_react/";

    // ===== Dota2 国服 datafeed（英雄中文本地化 / 头图详情） =====
    public const string Dota2CnDatafeedBase = "https://www.dota2.com.cn/datafeed/";
    public const string HeroListUrl = Dota2CnDatafeedBase + "heroList?task=herolist";
    public const string HeroDataUrl = Dota2CnDatafeedBase + "herodata?language=schinese&hero_id={0}";

    // ===== Steam 社区（头像框抓取） =====
    public const string SteamProfileBase = "https://steamcommunity.com/profiles/";

    // ===== GitHub（升级检查 + Releases 页面） =====
    public const string GitHubApiBase = "https://api.github.com/";
    public const string GitHubRepoName = "ShenHaoCore/DotaInsight";
    public const string GitHubReleasesApi = $"https://api.github.com/repos/{GitHubRepoName}/releases/latest";
    public const string GitHubReleasesPage = $"https://github.com/{GitHubRepoName}/releases/latest";
}
