using Serilog;
using System.Net;
using System.Net.Http;
using System.IO;
using DotaInsight.Models;

namespace DotaInsight.Services;

/// <summary>
/// 代理感知的 HTTP 主通道：路由优先级 = 手动配置 &gt; 自动检测 &gt; 直连。
/// 直连与代理两条 <see cref="SocketsHttpHandler"/> 各自持有独立连接池，
/// 每个请求按当前网络状态即时选路——无论手动保存还是自动检测，下一个请求就走新通道，无需重启。
/// 通道地址变更时按需重建（旧通道上在飞的请求不受影响）。
///
/// 为什么不用 client 层的 UseProxy：IHttpClientFactory 的 handler 池轮换周期约 2 分钟，
/// 换代理要等轮换才生效，用户会以为没效果。把选路做进主通道内部，切换是即时的。
///
/// 直连通道保持默认系统代理语义（与原 HttpClientHandler 一致）；
/// 打开自动解压——被掐的线路上 gzip 能显著省流量。
/// </summary>
public sealed class ProxySwitchHandler : HttpMessageHandler
{
    private readonly INetworkSettingsService _settings;
    private readonly IAutoProxyService _autoProxy;
    private readonly SocketsHttpHandler _direct;
    private readonly HttpMessageInvoker _directInvoker;
    private readonly object _sync = new();
    private SocketsHttpHandler _proxied;
    private HttpMessageInvoker _proxiedInvoker;
    private string _proxiedUrl = string.Empty;

    public ProxySwitchHandler(INetworkSettingsService settings, IAutoProxyService autoProxy)
    {
        _settings = settings;
        _autoProxy = autoProxy;
        Log.Information("ProxySwitchHandler 创建（实例化主通道）");
        _direct = CreateHandler();
        // 直连通道换多 IP 竞速连接：单个 Cloudflare 边缘 IP 被丢包时（netstat 呈 SYN_SENT）
        // 自动落到同一域名的其他可达 IP。系统代理语义保持：有系统代理则走 CONNECT 隧道。
        _direct.ConnectCallback = DirectConnectCallback;
        _directInvoker = new HttpMessageInvoker(_direct);
        _proxied = CreateHandler();
        _proxiedInvoker = new HttpMessageInvoker(_proxied);
    }

    private async ValueTask<Stream> DirectConnectCallback(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var proxyUri = HttpClient.DefaultProxy.GetProxy(
            new Uri($"https://{context.DnsEndPoint.Host}:{context.DnsEndPoint.Port}"));
        var proxy = proxyUri is null ? null : new WebProxy(proxyUri);
        Log.Information("直连竞速开始 {Host}（代理={ViaProxy}）", context.DnsEndPoint.Host, proxy is null ? "无" : proxyUri);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var stream = await ResilientConnector.ConnectStreamAsync(context.DnsEndPoint, proxy, cancellationToken)
            .ConfigureAwait(false);
        Log.Debug(
            "直连竞速完成 {Host}（代理={ViaProxy}，{Elapsed}ms）",
            context.DnsEndPoint.Host,
            proxy is null ? "无" : proxyUri,
            sw.ElapsedMilliseconds);
        return stream;
    }

    private static SocketsHttpHandler CreateHandler() => new()
    {
        AutomaticDecompression = DecompressionMethods.All,
        // 定期废弃长连接：网络切换 / 代理开关后不用等 TCP 半死连接自己超时
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => ActiveInvoker().SendAsync(request, cancellationToken);

    /// <summary>当前应使用的代理地址：手动配置 &gt; 自动检测 &gt; null（直连）。</summary>
    internal static string? ResolveProxyUrl(string manualUrl, string? autoDetectedUrl)
    {
        if (!string.IsNullOrWhiteSpace(manualUrl))
        {
            return NetworkSettings.TryParseProxyUrl(manualUrl, out _) ? manualUrl.Trim() : null;
        }

        return autoDetectedUrl;
    }

    private HttpMessageInvoker ActiveInvoker()
    {
        var url = ResolveProxyUrl(_settings.Current.ProxyUrl.Trim(), _autoProxy.DetectedProxyUrl);
        if (url is null || !NetworkSettings.TryParseProxyUrl(url, out var proxy))
        {
            return _directInvoker;
        }

        lock (_sync)
        {
            if (!string.Equals(_proxiedUrl, url, StringComparison.OrdinalIgnoreCase))
            {
                _proxiedUrl = url;
                _proxied = CreateHandler();
                _proxied.Proxy = proxy;
                _proxiedInvoker = new HttpMessageInvoker(_proxied);
            }

            return _proxiedInvoker;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _directInvoker.Dispose();
            _proxiedInvoker.Dispose();
        }

        base.Dispose(disposing);
    }
}
