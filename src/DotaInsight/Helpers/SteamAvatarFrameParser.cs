namespace DotaInsight.Helpers;

/// <summary>
/// steamcommunity 个人资料页 HTML 的头像框提取（纯函数，离线可测）。
/// 头像框是点数商店装扮，OpenDota 不透出，只能解析资料页：
/// 装备了框时页面有 <c>&lt;div class="profile_avatar_frame"&gt;&lt;img src="…"&gt;</c>；
/// 没装备时该元素不存在。网络抓取与缓存放在 <see cref="Services.SteamAvatarFrameService"/>。
/// </summary>
public static class SteamAvatarFrameParser
{
    /// <summary>
    /// 从资料页 HTML 提取头像框图片地址；无框 / 标记不可信时返回 null。
    /// 框图 CDN 随 Steam 迁移变化（曾为 community.*/economy/image，现为
    /// shared.cdn.queniuqe.com/community_assets/...），因此只校验"是 http 图片"，
    /// 不锁定 CDN 域名；结构特异性由「frame div 内第一个 img」保证。
    /// </summary>
    public static string? ExtractFrameUrl(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var marker = html.IndexOf("profile_avatar_frame", StringComparison.Ordinal);
        if (marker < 0)
        {
            return null;
        }

        var imgStart = html.IndexOf("<img", marker, StringComparison.OrdinalIgnoreCase);
        if (imgStart < 0)
        {
            return null;
        }

        var srcKey = html.IndexOf("src=", imgStart, StringComparison.OrdinalIgnoreCase);
        if (srcKey < 0)
        {
            return null;
        }

        var valueStart = srcKey + "src=".Length;
        // 跳过属性值前的空白，取引号内的内容（Steam 页面用双引号，兼容单引号）
        while (valueStart < html.Length && char.IsWhiteSpace(html[valueStart]))
        {
            valueStart++;
        }

        if (valueStart >= html.Length || html[valueStart] is not ('"' or '\''))
        {
            return null;
        }

        var quote = html[valueStart];
        var valueEnd = html.IndexOf(quote, valueStart + 1);
        if (valueEnd < 0)
        {
            return null;
        }

        var url = html.Substring(valueStart + 1, valueEnd - valueStart - 1).Trim();
        if (url.StartsWith("//", StringComparison.Ordinal))
        {
            url = "https:" + url;
        }

        // 排除头像本尊等非框图（框图在 community_assets / economy 等 CDN 路径下）
        var isImage = url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            && !url.Contains("avatars.steamstatic.com", StringComparison.OrdinalIgnoreCase)
            && (url.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                || url.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                || url.Contains("economy/image", StringComparison.OrdinalIgnoreCase)
                || url.Contains("community_assets", StringComparison.OrdinalIgnoreCase));
        return isImage ? url : null;
    }
}
