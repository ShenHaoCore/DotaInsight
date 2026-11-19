using System.Net;
using System.Net.Http;
using DotaInsight.Models;
using Serilog;

namespace DotaInsight.Services;

/// <summary>
/// 自动网络环境检测：直连优先；直连不可用时自动扫描本机常见加速器端口，
/// 找到能通 OpenDota 的就自动走它；直连恢复后自动切回。用户零配置。
///
/// 触发时机：应用启动 + 周期探测（45s）+ 请求失败信号（<see cref="ReportFailure"/>，限频 30s）。
/// 只扫 127.0.0.1——本机加速器端口，绝不涉及局域网/外部地址。
/// 用户手动配置的代理（INetworkSettingsService）优先级高于自动检测结果，此时探测让位。
/// </summary>
public interface IAutoProxyService
{
    /// <summary>自动检测到的本机代理地址（http://… 或 socks5://…）；null = 未发现，走直连。</summary>
    string? DetectedProxyUrl { get; }

    /// <summary>请求级失败信号：触发立即重估（内部限频）。</summary>
    void ReportFailure();

    /// <summary>立即重估一次（后台执行，限频内调用会被忽略）。</summary>
    void EvaluateNow();
}

public sealed class AutoProxyService : IAutoProxyService
{
    /// <summary>本机常见加速器端口：Clash(7890) / Clash Verge(7897) / v2rayN(10808 socks、10809 http) 等。</summary>
    private static readonly int[] CandidatePorts = [7890, 7897, 10809, 10808, 1080, 2080, 8118, 8888];

    /// <summary>
    /// 探测目标：/api/heroes（200，约 22KB）。注意文档里的 /api/status 实测返回 404，
    /// 不能当探针——这是 E2E 实测发现的，单测发现不了。
    /// </summary>
    private const string ProbeUrl = "https://api.opendota.com/api/heroes";

    private static readonly TimeSpan DirectProbeTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan ProxyProbeTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MinReevaluateInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PeriodicInterval = TimeSpan.FromSeconds(45);

    private readonly INetworkSettingsService _settings;
    private readonly Func<IWebProxy?, CancellationToken, Task<bool>> _probe;
    private readonly ILogger _logger;
    private readonly Timer _timer;
    private readonly object _sync = new();
    private string? _detectedUrl;
    private DateTime _lastEvalUtc = DateTime.MinValue;
    private int _evaluating;

    public AutoProxyService(INetworkSettingsService settings, ILogger logger)
        : this(settings, DefaultProbe, logger)
    {
    }

    /// <summary>探测函数可注入：单测用假探针驱动状态机，生产用真实 HTTP 探测。</summary>
    public AutoProxyService(
        INetworkSettingsService settings,
        Func<IWebProxy?, CancellationToken, Task<bool>> probe,
        ILogger logger)
    {
        _settings = settings;
        _probe = probe;
        _logger = logger.ForContext<AutoProxyService>();
        // 延迟首探：给应用启动让路，之后周期自愈（直连恢复自动切回）
        _timer = new Timer(_ => EvaluateNow(), null, PeriodicInterval, PeriodicInterval);
    }

    public string? DetectedProxyUrl
    {
        get
        {
            lock (_sync)
            {
                return _detectedUrl;
            }
        }
    }

    public void ReportFailure()
    {
        DateTime last;
        lock (_sync)
        {
            last = _lastEvalUtc;
        }

        if (DateTime.UtcNow - last < MinReevaluateInterval)
        {
            return;
        }

        EvaluateNow();
    }

    public void EvaluateNow()
    {
        if (Interlocked.CompareExchange(ref _evaluating, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await RunEvaluationAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "自动网络检测异常");
            }
            finally
            {
                Interlocked.Exchange(ref _evaluating, 0);
            }
        });
    }

    /// <summary>单次评估：可等待（单测直调）；EvaluateNow 的后台执行体。</summary>
    internal async Task RunEvaluationAsync()
    {
        try
        {
            // 手动代理优先：用户已接管，自动检测让位（探测也省了）
            if (!string.IsNullOrWhiteSpace(_settings.Current.ProxyUrl))
            {
                return;
            }

            var newUrl = await EvaluateCoreAsync().ConfigureAwait(false);
            string? previous;
            lock (_sync)
            {
                previous = _detectedUrl;
                if (newUrl != previous)
                {
                    _detectedUrl = newUrl;
                }
            }

            if (newUrl != previous)
            {
                _logger.Information(
                    "自动网络检测：{From} → {To}",
                    Describe(previous),
                    Describe(newUrl));
            }
        }
        finally
        {
            lock (_sync)
            {
                _lastEvalUtc = DateTime.UtcNow;
            }
        }
    }

    /// <summary>直连通就直连；不通则逐个尝试候选端口，取第一个能通 OpenDota 的。</summary>
    private async Task<string?> EvaluateCoreAsync()
    {
        if (await _probe(null, CancellationToken.None).ConfigureAwait(false))
        {
            return null;
        }

        foreach (var port in CandidatePorts)
        {
            // 先按 http 代理探（Clash 系端口多为 mixed，http/socks 同端口）；不通再按 socks5
            foreach (var scheme in new[] { "http", "socks5" })
            {
                var url = $"{scheme}://127.0.0.1:{port}";
                if (NetworkSettings.TryParseProxyUrl(url, out var proxy)
                    && await _probe(proxy, CancellationToken.None).ConfigureAwait(false))
                {
                    return url;
                }
            }
        }

        return null;
    }

    private static string Describe(string? url) => url ?? "直连";

    /// <summary>真实探测：通过给定代理（null = 直连/系统代理语义）请求 OpenDota 状态接口，200 即通。</summary>
    private static async Task<bool> DefaultProbe(IWebProxy? proxy, CancellationToken cancellationToken)
    {
        try
        {
            using var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(1)
            };
            if (proxy is not null)
            {
                handler.Proxy = proxy;
            }

            // 探测同样用多 IP 竞速：单 IP 被丢包时不误判「直连不可用」
            handler.ConnectCallback = (ctx, ct) =>
                ResilientConnector.ConnectStreamAsync(ctx.DnsEndPoint, proxy, ct);

            using var client = new HttpClient(handler)
            {
                Timeout = proxy is null ? DirectProbeTimeout : ProxyProbeTimeout
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("DotaInsight/1.0");

            using var resp = await client.GetAsync(ProbeUrl, cancellationToken).ConfigureAwait(false);
            return (int)resp.StatusCode == 200;
        }
        catch
        {
            return false;
        }
    }
}
