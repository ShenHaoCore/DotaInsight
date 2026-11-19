using System.Net;
using DotaInsight.Helpers;
using DotaInsight.Models;
using DotaInsight.Services;
using Serilog;
using Serilog.Core;

namespace DotaInsight.Tests;

/// <summary>
/// 网络设置：代理地址解析、存取、用户资产保护。
/// 用内存缓存代替 LiteDB，避免测试触碰用户的真实 cache.db。
/// </summary>
public class NetworkSettingsTests
{
    private static readonly ILogger SilentLogger = new LoggerConfiguration().CreateLogger();

    private sealed class InMemoryCache : ILiteDbCacheService
    {
        private readonly Dictionary<string, string> _store = new(StringComparer.Ordinal);

        public T? Get<T>(string key)
            => _store.TryGetValue(key, out var json)
                ? System.Text.Json.JsonSerializer.Deserialize<T>(json)
                : default;

        public T? GetStale<T>(string key) => Get<T>(key);

        public void Set<T>(string key, T value, TimeSpan? ttl = null)
        {
            ArgumentNullException.ThrowIfNull(value);
            _store[key] = System.Text.Json.JsonSerializer.Serialize(value);
        }

        public int ClearAll()
        {
            var count = _store.Count;
            _store.Clear();
            return count;
        }
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("127.0.0.1:7890", false)]      // 缺协议：常见误输入，拒绝并让用户补全
    [InlineData("ftp://127.0.0.1:21", false)]  // 不支持的协议
    [InlineData("http://", false)]
    [InlineData("http://127.0.0.1:7890", true)]
    [InlineData("socks5://127.0.0.1:7890", true)]
    [InlineData("https://proxy.example.com", true)]
    public void TryParseProxyUrl_AcceptsOnlySupportedAbsoluteUrls(string input, bool expected)
    {
        var ok = NetworkSettings.TryParseProxyUrl(input, out var proxy);
        Assert.Equal(expected, ok);
        if (!expected)
        {
            return;
        }

        // 地址正确落到 WebProxy 上（host + 端口）
        var address = Assert.IsType<WebProxy>(proxy).Address;
        Assert.False(string.IsNullOrEmpty(address?.Host));
    }

    [Fact]
    public void TryParseProxyUrl_FillsDefaultPortPerScheme()
    {
        Assert.True(NetworkSettings.TryParseProxyUrl("http://proxy.example.com", out var httpProxy));
        Assert.Equal(80, ((WebProxy)httpProxy).Address?.Port);

        Assert.True(NetworkSettings.TryParseProxyUrl("socks5://proxy.example.com", out var socksProxy));
        Assert.Equal(1080, ((WebProxy)socksProxy).Address?.Port);

        Assert.True(NetworkSettings.TryParseProxyUrl("https://proxy.example.com", out var httpsProxy));
        Assert.Equal(443, ((WebProxy)httpsProxy).Address?.Port);
    }

    [Fact]
    public void Save_RoundTripsAndAppliesImmediately()
    {
        var service = new NetworkSettingsService(new InMemoryCache(), SilentLogger);
        Assert.Null(service.ResolveProxy());

        service.Save(new NetworkSettings { ProxyUrl = "http://127.0.0.1:7890" });
        var proxy = Assert.IsType<WebProxy>(service.ResolveProxy());
        Assert.Equal("127.0.0.1", proxy.Address?.Host);
        Assert.Equal(7890, proxy.Address?.Port);

        // 清空 = 回到直连
        service.Save(new NetworkSettings());
        Assert.Null(service.ResolveProxy());
    }

    [Fact]
    public void Save_PersistsAcrossServiceInstances()
    {
        var cache = new InMemoryCache();
        var first = new NetworkSettingsService(cache, SilentLogger);
        first.Save(new NetworkSettings { ProxyUrl = "socks5://127.0.0.1:7890" });

        // 新实例（模拟重启）从磁盘读回
        var second = new NetworkSettingsService(cache, SilentLogger);
        Assert.Equal("socks5://127.0.0.1:7890", second.Current.ProxyUrl);
        Assert.NotNull(second.ResolveProxy());
    }

    [Fact]
    public void Save_RaisesChangedEvent()
    {
        var service = new NetworkSettingsService(new InMemoryCache(), SilentLogger);
        var fired = 0;
        service.Changed += () => fired++;

        service.Save(new NetworkSettings());
        Assert.Equal(1, fired);
    }

    [Fact]
    public void NetworkSettingsKey_IsUserAsset()
    {
        // 清缓存时必须保留代理设置
        Assert.True(CacheKeys.IsUserAsset(CacheKeys.NetworkSettings));
    }
}
