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

    /// <summary>
    /// 远程图片磁盘缓存（英雄头像 / 技能图 / 头图封面）。
    /// 文件按完整 URL 哈希命名，并会把超大素材降采样后落盘。
    /// </summary>
    public static string ImageCache { get; } = Path.Combine(Root, "image-cache");

    /// <summary>
    /// 早期版本的图片缓存目录（存原图、不降采样）。升级后一次性清理，回收磁盘。
    /// </summary>
    public static string LegacyImageCache { get; } = Path.Combine(Root, "hero-icons");

    /// <summary>LiteDB 数据缓存库。</summary>
    public static string Database { get; } = Path.Combine(Root, "cache.db");

    /// <summary>日志目录。</summary>
    public static string Logs { get; } = Path.Combine(Root, "logs");

    /// <summary>确保应用数据目录存在。</summary>
    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ImageCache);
        Directory.CreateDirectory(Logs);
    }
}
