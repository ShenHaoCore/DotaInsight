using System.Text;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using DotaInsight.Models;
using DotaInsight.Services;
using Serilog;

namespace DotaInsight.Tests;

/// <summary>
/// 战绩分析的三层缓存语义：新鲜缓存直出、过期缓存秒开+后台刷新、强刷旁路。
/// 全部走离线 stub（HTTP 层直接抛异常），不触碰真实网络。
/// </summary>
public sealed class MatchAnalysisCacheTests : IDisposable
{
    private static readonly ILogger SilentLog = new LoggerConfiguration().CreateLogger();

    private readonly string _dir;

    public MatchAnalysisCacheTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "dotainsight-matchcache-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>所有请求直接抛异常，模拟整条线路被掐。</summary>
    private sealed class OfflineHandler : HttpMessageHandler
    {
        public int Requests;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            throw new HttpRequestException("模拟离线");
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler)
        {
            // 与生产配置对齐：opendota 客户端带 BaseAddress，否则相对 URL 进不了 handler 就抛
            BaseAddress = new Uri("https://api.opendota.com/")
        };
    }

    /// <summary>按 URL 应答标准 JSON 的假服务端（后台刷新成功路径用）。</summary>
    private sealed class CannedHandler : HttpMessageHandler
    {
        public int Requests;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            var url = request.RequestUri?.PathAndQuery ?? "";
            string json;
            if (url.Contains("/wl"))
            {
                json = "{\"win\":5,\"lose\":3}";
            }
            else if (url.Contains("/recentMatches"))
            {
                json = "[]";
            }
            else if (url.Contains("heroStats"))
            {
                json = "[]";
            }
            else
            {
                json = "{\"profile\":{\"account_id\":10086,\"personaname\":\"新昵称\"},\"rank_tier\":85}";
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, new System.Net.Http.Headers.MediaTypeHeaderValue("application/json"))
            });
        }
    }

    // 与 MatchAnalysisService.CachePrefix 一致（private const，测试用同串）
    private const string CachePrefix = "opendota:matchAnalysis:v2:";

    private static MatchAnalysisResult ResultFor(long accountId) => new()
    {
        Profile = new PlayerProfile { AccountId = accountId, PersonaName = $"测试账号{accountId}" }
    };

    private sealed class StubAutoProxy : IAutoProxyService
    {
        public string? DetectedProxyUrl => null;
        public void ReportFailure() { }
        public void EvaluateNow() { }
    }

    private MatchAnalysisService CreateService(out OfflineHandler handler, out LiteDbCacheService cache)
    {
        handler = new OfflineHandler();
        cache = new LiteDbCacheService(SilentLog, Path.Combine(_dir, $"{Guid.NewGuid():N}.db"));
        var factory = new StubHttpClientFactory(handler);
        var localization = new HeroLocalizationService(factory, cache, SilentLog);
        var heroCounter = new HeroCounterService(factory, cache, localization, SilentLog);
        var itemCatalog = new ItemCatalogService(factory, cache, SilentLog);
        return new MatchAnalysisService(factory, cache, heroCounter, itemCatalog, new StubAutoProxy(), SilentLog);
    }

    private MatchAnalysisService CreateService(out LiteDbCacheService cache, HttpMessageHandler handler)
    {
        cache = new LiteDbCacheService(SilentLog, Path.Combine(_dir, $"{Guid.NewGuid():N}.db"));
        var factory = new StubHttpClientFactory(handler);
        var localization = new HeroLocalizationService(factory, cache, SilentLog);
        var heroCounter = new HeroCounterService(factory, cache, localization, SilentLog);
        var itemCatalog = new ItemCatalogService(factory, cache, SilentLog);
        return new MatchAnalysisService(factory, cache, heroCounter, itemCatalog, new StubAutoProxy(), SilentLog);
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.True(condition(), "等待条件超时");
    }

    [Fact]
    public async Task FreshCache_ServesWithoutNetwork()
    {
        var service = CreateService(out var handler, out var cache);
        cache.Set($"{CachePrefix}10086", ResultFor(10086), TimeSpan.FromHours(1));

        var result = await service.AnalyzeAsync(10086);

        Assert.True(result.FromCache);
        Assert.False(result.IsOffline);
        Assert.Equal("测试账号10086", result.Profile?.PersonaName);
        Assert.Equal(0, handler.Requests);
        cache.Dispose();
    }

    [Fact]
    public async Task ExpiredCache_ServesStaleInstantly_AndRefreshesInBackgroundOnce()
    {
        var service = CreateService(out var handler, out var cache);
        // 负 TTL = 立即过期
        cache.Set($"{CachePrefix}10086", ResultFor(10086), TimeSpan.FromMilliseconds(-10));

        var result = await service.AnalyzeAsync(10086);

        // 秒开旧数据，不碰网络（此刻 handler 还是 0 次）
        Assert.True(result.FromCache);
        Assert.True(result.IsOffline);
        Assert.True(result.RefreshingInBackground);
        Assert.Equal("测试账号10086", result.Profile?.PersonaName);
        Assert.Equal(0, handler.Requests);

        // 后台刷新异步发起并失败（离线 stub）
        await WaitForAsync(() => handler.Requests >= 1);
        var countAfterFirst = handler.Requests;

        // 60s 去重窗口内再进页面：不再拉起第二个后台刷新
        var again = await service.AnalyzeAsync(10086);
        Assert.True(again.RefreshingInBackground);
        await Task.Delay(300);
        Assert.Equal(countAfterFirst, handler.Requests);
        cache.Dispose();
    }

    [Fact]
    public async Task ForceRefresh_BypassesCache_FallsBackToStaleOnFailure()
    {
        var service = CreateService(out _, out var cache);
        cache.Set($"{CachePrefix}10086", ResultFor(10086), TimeSpan.FromMilliseconds(-10));

        // 强刷不看过期缓存：先走网络（离线 stub 抛异常）→ 失败回退旧数据
        var result = await service.AnalyzeAsync(10086, forceRefresh: true);

        Assert.True(result.FromCache);
        Assert.True(result.IsOffline);
        Assert.False(result.RefreshingInBackground);
        Assert.Equal("测试账号10086", result.Profile?.PersonaName);
        cache.Dispose();
    }

    [Fact]
    public async Task NoCache_Offline_Throws()
    {
        var service = CreateService(out _, out var cache);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => service.AnalyzeAsync(10086));
        cache.Dispose();
    }

    [Fact]
    public async Task Warmup_FreshCache_SkipsNetwork()
    {
        var service = CreateService(out var handler, out var cache);
        cache.Set($"{CachePrefix}10086", ResultFor(10086), TimeSpan.FromHours(1));

        // 新鲜缓存：零网络，直接返回
        await service.WarmupAccountAsync(10086);
        Assert.Equal(0, handler.Requests);
        cache.Dispose();
    }

    [Fact]
    public async Task Warmup_ExpiredCache_FetchesSilently_NoThrow()
    {
        var service = CreateService(out var handler, out var cache);
        cache.Set($"{CachePrefix}10086", ResultFor(10086), TimeSpan.FromMilliseconds(-10));

        // 过期：走网络（离线 stub 失败），静默不抛
        await service.WarmupAccountAsync(10086);
        Assert.True(handler.Requests >= 1);
        cache.Dispose();
    }

    [Fact]
    public void NetworkGate_TracksUserBusy()
    {
        INetworkGate gate = new NetworkGate();
        Assert.False(gate.IsUserBusy);

        using (gate.BeginUserOperation())
        {
            Assert.True(gate.IsUserBusy);
            using (gate.BeginUserOperation())
            {
                Assert.True(gate.IsUserBusy);   // 嵌套计数
            }
            Assert.True(gate.IsUserBusy);
        }

        Assert.False(gate.IsUserBusy);
    }

    [Fact]
    public async Task ExpiredCache_BackgroundRefresh_CallbackInvokedWithFreshData()
    {
        var service = CreateService(out var cache, new CannedHandler());
        cache.Set($"{CachePrefix}10086", ResultFor(10086), TimeSpan.FromMilliseconds(-10));

        MatchAnalysisResult? notified = null;
        var result = await service.AnalyzeAsync(10086, onBackgroundRefreshed: r => notified = r);

        // 先秒开旧数据
        Assert.True(result.RefreshingInBackground);
        Assert.Equal("测试账号10086", result.Profile?.PersonaName);

        // 后台拉新成功 → 回调收到新数据，缓存同步更新
        await WaitForAsync(() => notified is not null);
        Assert.Equal("新昵称", notified!.Profile?.PersonaName);
        Assert.False(notified.RefreshingInBackground);
        Assert.Equal("新昵称", cache.Get<MatchAnalysisResult>($"{CachePrefix}10086")?.Profile?.PersonaName);
        cache.Dispose();
    }

    [Fact]
    public async Task ExpiredCache_Offline_BackgroundCallbackInvokedWithNull()
    {
        var service = CreateService(out _, out var cache);   // 离线 stub：后台刷新必然失败
        cache.Set($"{CachePrefix}10086", ResultFor(10086), TimeSpan.FromMilliseconds(-10));

        var notifiedState = 0; // 0=未通知 -1=失败(null) 1=成功
        await service.AnalyzeAsync(10086, onBackgroundRefreshed: r => notifiedState = r is null ? -1 : 1);

        await WaitForAsync(() => notifiedState != 0);
        Assert.Equal(-1, notifiedState);
        cache.Dispose();
    }
}
