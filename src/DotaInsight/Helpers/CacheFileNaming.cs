using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace DotaInsight.Helpers;

/// <summary>
/// 按完整 URL 生成磁盘缓存文件名，避免同名不同路径互相覆盖。
/// </summary>
public static class CacheFileNaming
{
    public static string FromUrl(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))
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
