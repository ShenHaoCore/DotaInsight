using System.Text.Json;

namespace DotaInsight.Helpers;

/// <summary>
/// 一次可用的升级信息，等价于 AutoUpdater 的 AppCast item。
/// </summary>
/// <param name="Version">四段版本号（v0.2.0 → 0.2.0.0），与 AssemblyVersion 同形，可直接比较。</param>
/// <param name="TagName">原始标签名，如 v0.2.0。</param>
/// <param name="DownloadUrl">升级包（zip）下载地址。</param>
/// <param name="ChangelogUrl">Release 页面地址。</param>
/// <param name="Notes">Release 说明正文（截断后），可选。</param>
public sealed record GitHubReleaseInfo(
    Version Version,
    string TagName,
    string DownloadUrl,
    string ChangelogUrl,
    string Notes)
{
    /// <summary>形如 v0.2.0 的展示串。</summary>
    public string VersionText => $"v{Version.ToString(3)}";
}

/// <summary>
/// GitHub Releases API（/releases/latest）JSON → 升级信息。
/// 启动检查（AutoUpdater 的 ParseUpdateInfoEvent）与手动检查共用这一份解析，
/// 全库唯一实现，便于单测覆盖。
/// </summary>
public static class GitHubReleaseParser
{
    /// <summary>升级包资产名后缀，对应 release.yml 打出的 zip。</summary>
    public const string AssetSuffix = "-win-x64.zip";

    /// <summary>
    /// 资产名必须含该片段才认作 Windows x64 包。
    /// 不能退化成「任意 zip 都行」——真实仓库里常同时挂着 aarch64 / arm64 / GPO 等其它 zip
    /// （实测 ripgrep 会命中 aarch64 包），装错架构比明确报错糟糕得多。
    /// </summary>
    private const string PlatformHint = "win-x64";

    /// <summary>
    /// 解析 GitHub Release JSON。失败时返回 false，并给出可直接展示给用户的中文原因。
    /// </summary>
    public static bool TryParse(string? json, out GitHubReleaseInfo? info, out string error)
    {
        info = null;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "更新服务返回了空内容。";
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "更新信息格式无法识别。";
                return false;
            }

            if (root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
            {
                error = "最新版本仍是草稿，暂不提供更新。";
                return false;
            }

            var tag = GetString(root, "tag_name");
            if (string.IsNullOrWhiteSpace(tag))
            {
                error = "更新信息里没有版本号。";
                return false;
            }

            if (!TryParseVersion(tag, out var version))
            {
                error = $"无法识别的版本号：{tag}";
                return false;
            }

            var asset = FindAsset(root);
            if (asset is null)
            {
                error = $"版本 {tag} 没有提供 {AssetSuffix} 安装包。";
                return false;
            }

            info = new GitHubReleaseInfo(
                version,
                tag,
                asset.Value.Url,
                GetString(root, "html_url"),
                Truncate(GetString(root, "body"), 4000));
            return true;
        }
        catch (JsonException ex)
        {
            error = $"解析更新信息失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 标签号 → 四段版本（v0.2.0 → 0.2.0.0）。
    /// 补齐到四段是为了与 AssemblyVersion（恒为 x.y.z.0）可直接比较：
    /// 否则 <c>new Version("0.2.0")</c> 的 Build=-1，与 0.2.0.0 比较会判定为「更小」而漏掉更新。
    /// </summary>
    public static bool TryParseVersion(string? tag, out Version version)
    {
        version = new Version(0, 0, 0, 0);

        if (string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }

        var text = tag.Trim().TrimStart('v', 'V');
        if (!Version.TryParse(text, out var parsed))
        {
            return false;
        }

        version = new Version(
            parsed.Major,
            parsed.Minor,
            parsed.Build < 0 ? 0 : parsed.Build,
            parsed.Revision < 0 ? 0 : parsed.Revision);
        return true;
    }

    /// <summary>
    /// 优先取 <see cref="AssetSuffix"/> 结尾的资产；命名略有出入时退而取「Windows x64 的 zip」。
    /// 找不到就返回 null（宁可报「没有提供安装包」，也不误装别的架构）。
    /// </summary>
    private static (string Name, string Url)? FindAsset(JsonElement root)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        (string Name, string Url)? relaxed = null;

        foreach (var asset in assets.EnumerateArray())
        {
            var name = GetString(asset, "name");
            var url = GetString(asset, "browser_download_url");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            if (name.EndsWith(AssetSuffix, StringComparison.OrdinalIgnoreCase))
            {
                return (name, url);
            }

            if (relaxed is null
                && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                && name.Contains(PlatformHint, StringComparison.OrdinalIgnoreCase))
            {
                relaxed = (name, url);
            }
        }

        return relaxed;
    }

    private static string GetString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string Truncate(string text, int maxLength)
        => text.Length <= maxLength ? text : text[..maxLength] + "…";
}
