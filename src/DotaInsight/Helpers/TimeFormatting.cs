namespace DotaInsight.Helpers;

/// <summary>
/// 时长格式化的唯一实现：不足 1 小时显示 mm:ss，达到 1 小时显示 h:mm:ss。
/// </summary>
internal static class TimeFormatting
{
    public static string FormatDuration(int seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return span.TotalHours >= 1
            ? span.ToString(@"h\:mm\:ss")
            : span.ToString(@"mm\:ss");
    }
}
