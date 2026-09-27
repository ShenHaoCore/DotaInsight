using System.IO;

namespace DotaInsight.Helpers;

/// <summary>
/// 安装版数据目录：统一落在 %LocalAppData%\DotaInsight\。
/// </summary>
public static class AppPaths
{
    /// <summary>应用根目录。</summary>
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DotaInsight");

    /// <summary>英雄头像磁盘缓存。</summary>
    public static string HeroIcons { get; } = Path.Combine(Root, "hero-icons");

    /// <summary>LiteDB 数据缓存库。</summary>
    public static string Database { get; } = Path.Combine(Root, "cache.db");

    /// <summary>日志目录。</summary>
    public static string Logs { get; } = Path.Combine(Root, "logs");

    /// <summary>确保应用数据目录存在。</summary>
    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(HeroIcons);
        Directory.CreateDirectory(Logs);
    }
}
