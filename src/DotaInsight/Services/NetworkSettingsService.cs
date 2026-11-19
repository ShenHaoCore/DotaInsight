using System.Net;
using DotaInsight.Helpers;
using DotaInsight.Models;
using Serilog;

namespace DotaInsight.Services;

/// <summary>
/// 网络设置：代理地址的存取与变更广播。
/// 数据放 <see cref="CacheKeys.NetworkSettings"/> 条目里，该键是用户资产，清缓存时保留。
/// 保存立即生效——HTTP 通道（<see cref="ProxySwitchHandler"/>）订阅 <see cref="Changed"/> 按需重建。
/// </summary>
public interface INetworkSettingsService
{
    /// <summary>当前生效的设置快照。</summary>
    NetworkSettings Current { get; }

    /// <summary>解析当前代理地址为 <see cref="IWebProxy"/>；未配置或无效返回 null（直连）。</summary>
    IWebProxy? ResolveProxy();

    /// <summary>保存设置（写盘 + 广播 <see cref="Changed"/>）。</summary>
    void Save(NetworkSettings settings);

    /// <summary>设置保存后触发。</summary>
    event Action? Changed;
}

public sealed class NetworkSettingsService : INetworkSettingsService
{
    /// <summary>网络设置与账号一样是长期数据，TTL 取得足够长等于不过期。</summary>
    private static readonly TimeSpan Retention = TimeSpan.FromDays(3650);

    private readonly ILiteDbCacheService _cache;
    private readonly ILogger _logger;
    private readonly object _sync = new();
    private NetworkSettings _current;

    public NetworkSettingsService(ILiteDbCacheService cache, ILogger logger)
    {
        _cache = cache;
        _logger = logger.ForContext<NetworkSettingsService>();
        _current = Load();
    }

    public NetworkSettings Current
    {
        get
        {
            lock (_sync)
            {
                return _current;
            }
        }
    }

    public event Action? Changed;

    public IWebProxy? ResolveProxy()
    {
        lock (_sync)
        {
            return NetworkSettings.TryParseProxyUrl(_current.ProxyUrl, out var proxy) ? proxy : null;
        }
    }

    public void Save(NetworkSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        lock (_sync)
        {
            _current = settings;
            try
            {
                _cache.Set(CacheKeys.NetworkSettings, settings, Retention);
                _logger.Information("网络设置已保存 ProxyUrl='{ProxyUrl}'", settings.ProxyUrl);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "保存网络设置失败");
            }
        }

        Changed?.Invoke();
    }

    private NetworkSettings Load()
    {
        try
        {
            return _cache.Get<NetworkSettings>(CacheKeys.NetworkSettings) ?? new NetworkSettings();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "读取网络设置失败，按直连处理");
            return new NetworkSettings();
        }
    }
}
