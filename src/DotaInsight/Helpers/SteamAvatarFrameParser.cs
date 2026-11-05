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
    /// 框图一律经 economy CDN 下发，因此要求 src 含 economy/image，
    /// 防止把页面里紧邻的其他图片（头像、表情）误认成框。
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

        return url.Contains("economy/image", StringComparison.OrdinalIgnoreCase)
            && url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? url
                : null;
    }
}
