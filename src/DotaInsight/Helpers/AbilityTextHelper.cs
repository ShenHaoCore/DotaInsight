using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace DotaInsight.Helpers;

/// <summary>
/// 国服技能文案：去 HTML，并用 special_values 替换 %token%。
/// </summary>
public static class AbilityTextHelper
{
    private static readonly Regex HtmlTagRegex =
        new("<.*?>", RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex BrRegex =
        new("<br\\s*/?>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex TokenRegex =
        new("%([a-zA-Z0-9_]+)%", RegexOptions.Compiled);

    private static readonly Regex TalentTokenRegex =
        new(@"\{s:([a-zA-Z0-9_]+)\}", RegexOptions.Compiled);

    public static string StripHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var text = BrRegex.Replace(html, "\n");
        text = HtmlTagRegex.Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text);
        return text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
    }

    /// <summary>
    /// 先去 HTML，再把 %name% 换成 special_values；剩余 %% 视为字面量 %。
    /// </summary>
    public static string FormatDescription(
        string? html,
        IReadOnlyDictionary<string, string>? values)
    {
        var text = StripHtml(html);
        if (string.IsNullOrEmpty(text) || values is null || values.Count == 0)
        {
            return ReplaceLiteralPercents(text);
        }

        text = TokenRegex.Replace(text, match =>
        {
            var key = match.Groups[1].Value;
            return values.TryGetValue(key, out var value) ? value : match.Value;
        });

        return ReplaceLiteralPercents(text);
    }

    /// <summary>
    /// 替换天赋名中的 {s:value} / {s:bonus_xxx} 令牌；无法解析时保留原文。
    /// </summary>
    public static string FormatTalentName(
        string? template,
        Func<string, string?> resolve)
    {
        var text = StripHtml(template);
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return TalentTokenRegex.Replace(text, match =>
            resolve(match.Groups[1].Value) ?? match.Value).Trim();
    }

    /// <summary>单个数值格式化（整数去小数点，否则保留两位内）。</summary>
    public static string FormatValue(double value) => FormatNumber(value);

    /// <summary>
    /// 多级数值用 / 连接，例如 25/30/35/40。
    /// </summary>
    public static string FormatSpecialValue(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            return string.Empty;
        }

        return string.Join('/', values.Select(FormatNumber));
    }

    private static string ReplaceLiteralPercents(string text)
        => string.IsNullOrEmpty(text)
            ? text
            : text.Replace("%%", "%", StringComparison.Ordinal);

    private static string FormatNumber(double value)
    {
        if (Math.Abs(value - Math.Round(value)) < 0.0001)
        {
            return ((long)Math.Round(value)).ToString(CultureInfo.InvariantCulture);
        }

        return value.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
