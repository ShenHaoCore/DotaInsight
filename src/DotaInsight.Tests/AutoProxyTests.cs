using System.Diagnostics;
using System.Net;
using DotaInsight.Models;
using DotaInsight.Services;
using Serilog;

namespace DotaInsight.Tests;

/// <summary>
/// 自动网络检测：直连优先、候选端口扫描、手动配置让位、失败信号限频。
/// 探针注入假实现（按脚本应答），不触碰真实网络。
/// </summary>
public sealed class AutoProxyTests
{
    private static readonly ILogger SilentLog = new LoggerConfiguration().CreateLogger();

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

    /// <summary>脚本化探针：按"该代理是否应该通"应答，并计数。</summary>
    private sealed class FakeProbe
    {
        private readonly Func<IWebProxy?, bool> _impl;

        public FakeProbe(Func<IWebProxy?, bool> impl) => _impl = impl;

        public int Calls;

        public Task<bool> ProbeAsync(IWebProxy? proxy, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(_impl(proxy));
        }
    }

    private static NetworkSettingsService CreateSettings()
        => new(new InMemoryCache(), SilentLog);

    /// <summary>探针判等：按 scheme+host+port 比较（WebProxy 无 ToString 保证）。</summary>
    private static string? ProxyKey(IWebProxy? proxy)
        => proxy is WebProxy wp ? wp.Address?.ToString() : null;

    private static AutoProxyService CreateService(
        NetworkSettingsService settings,
        FakeProbe probe)
        => new(settings, probe.ProbeAsync, SilentLog);

    private static readonly Func<IWebProxy?, bool> AllDead = _ => false;

    [Fact]
    public async Task DirectHealthy_PrefersDirect_EvenIfProxyWouldWork()
    {
        var settings = CreateSettings();
        // 直连通、所有候选代理也通：仍然选直连
        var probe = new FakeProbe(p => p is null || ProxyKey(p)!.Contains("7890"));
        var service = new AutoProxyService(settings, probe.ProbeAsync, SilentLog);

        await service.RunEvaluationAsync();

        Assert.Null(service.DetectedProxyUrl);
        Assert.True(probe.Calls >= 1);
    }

    [Fact]
    public async Task DirectDown_FindsFirstWorkingLocalProxy()
    {
        var settings = CreateSettings();
        // 直连不通；7890 不通，7897 (http) 通
        var probe = new FakeProbe(p =>
        {
            if (p is null)
            {
                return false;
            }

            var key = ProxyKey(p)!;
            return key.Contains("7897") && key.StartsWith("http");
        });
        var service = new AutoProxyService(settings, probe.ProbeAsync, SilentLog);

        await service.RunEvaluationAsync();

        Assert.Equal("http://127.0.0.1:7897", service.DetectedProxyUrl);
    }

    [Fact]
    public async Task ManualProxyConfigured_DetectionYields()
    {
        var settings = CreateSettings();
        settings.Save(new NetworkSettings { ProxyUrl = "http://127.0.0.1:7890" });
        var probe = new FakeProbe(_ => true);
        var service = new AutoProxyService(settings, probe.ProbeAsync, SilentLog);

        await service.RunEvaluationAsync();

        // 手动接管：探测一次都不该发
        Assert.Equal(0, probe.Calls);
        Assert.Null(service.DetectedProxyUrl);
    }

    [Fact]
    public async Task ReportFailure_IsRateLimited()
    {
        var settings = CreateSettings();
        var probe = new FakeProbe(AllDead);
        var service = new AutoProxyService(settings, probe.ProbeAsync, SilentLog);

        service.ReportFailure();
        await WaitForAsync(() => probe.Calls >= 1);
        // 等第一次评估完全收尾（_lastEvalUtc 落定）
        await Task.Delay(300);

        // 30s 限频内再次报告：不再评估
        var count = probe.Calls;
        service.ReportFailure();
        await Task.Delay(150);
        Assert.Equal(count, probe.Calls);
    }

    [Fact]
    public async Task DirectRecovery_SwitchesBackToDirect()
    {
        var settings = CreateSettings();
        var directUp = false;
        var probe = new FakeProbe(_ => directUp || _ is not null);
        var service = new AutoProxyService(settings, probe.ProbeAsync, SilentLog);

        // 直连挂 → 选到代理
        await service.RunEvaluationAsync();
        Assert.NotNull(service.DetectedProxyUrl);

        // 直连恢复 → 切回直连
        directUp = true;
        await service.RunEvaluationAsync();
        Assert.Null(service.DetectedProxyUrl);
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(30);
        }

        Assert.True(condition(), "等待条件超时");
    }
}

/// <summary>
/// 主通道路由优先级：手动配置 &gt; 自动检测 &gt; 直连。
/// </summary>
public sealed class ProxyRouteResolutionTests
{
    [Fact]
    public void ManualValid_WinsOverAuto()
    {
        var url = ProxySwitchHandler.ResolveProxyUrl("http://127.0.0.1:7890", "socks5://127.0.0.1:1080");
        Assert.Equal("http://127.0.0.1:7890", url);
    }

    [Fact]
    public void NoManual_UsesAuto()
    {
        var url = ProxySwitchHandler.ResolveProxyUrl("", "socks5://127.0.0.1:1080");
        Assert.Equal("socks5://127.0.0.1:1080", url);
    }

    [Fact]
    public void Neither_FallsBackToDirect()
    {
        Assert.Null(ProxySwitchHandler.ResolveProxyUrl("", null));
    }

    [Fact]
    public void InvalidManual_DisablesProxyRatherThanFallingThrough()
    {
        // 手动档填了但格式无效：按直连处理（保存入口有校验，这里只是防御）
        Assert.Null(ProxySwitchHandler.ResolveProxyUrl("not-a-proxy", "socks5://127.0.0.1:1080"));
    }
}
