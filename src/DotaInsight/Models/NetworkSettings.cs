using System.Net;
using System.Text.Json.Serialization;

namespace DotaInsight.Models;

/// <summary>
/// 网络设置：目前只有代理地址。空地址 = 直连。
/// 存 LiteDB 用户资产（清缓存不丢），保存后下一个请求即走新通道，无需重启。
/// </summary>
public sealed class NetworkSettings
{
    /// <summary>代理地址（http://host:port 或 socks5://host:port），空 = 直连。</summary>
    public string ProxyUrl { get; set; } = string.Empty;

    /// <summary>
    /// 解析用户输入的代理地址。支持 http / https / socks5，
    /// 省略端口时按协议默认值补齐（http 80 / https 443 / socks5 1080）。
    /// 空串与格式不对都返回 false（= 直连）。
    /// </summary>
    public static bool TryParseProxyUrl(string? url, out IWebProxy proxy)
    {
        proxy = new WebProxy();
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || uri.Host.Length == 0
            || uri.Scheme is not ("http" or "https" or "socks5"))
        {
            return false;
        }

        // 自定义 scheme（socks5）不写端口时 Uri.Port 返回 0，按协议默认值补齐
        var port = uri.Port > 0
            ? uri.Port
            : uri.Scheme switch
            {
                "https" => 443,
                "socks5" => 1080,
                _ => 80
            };

        proxy = new WebProxy(uri.Host, port);
        return true;
    }
}
