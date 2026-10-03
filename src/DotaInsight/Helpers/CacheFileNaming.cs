using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace DotaInsight.Helpers;

/// <summary>
/// 按完整 URL 生成磁盘缓存文件名，避免同名不同路径互相覆盖。
/// </summary>
public static class CacheFileNaming
{
    /// <summary>
    /// 生成缓存文件名。
    /// <paramref name="storeWidth"/> &gt; 0 时表示该文件按此宽度降采样落盘，
    /// 需与原始尺寸版本区分，避免互相覆盖。
    /// </summary>
    public static string FromUrl(string url, int storeWidth = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        var key = storeWidth > 0 ? $"{url}#w{storeWidth}" : url;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))
            .ToLowerInvariant();

        var ext = ".bin";
        try
        {
            var pathExt = Path.GetExtension(new Uri(url).AbsolutePath);
            if (!string.IsNullOrWhiteSpace(pathExt) && pathExt.Length is >= 2 and <= 5)
            {
                ext = pathExt.ToLowerInvariant();
            }
        }
        catch
        {
            // keep .bin
        }

        return hash + ext;
    }
}
