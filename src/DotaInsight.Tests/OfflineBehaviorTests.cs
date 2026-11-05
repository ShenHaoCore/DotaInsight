using System.IO;
using System.Net.Http;
using DotaInsight.Helpers;
using DotaInsight.Services;
using Serilog;

namespace DotaInsight.Tests;

/// <summary>
/// 本地化服务的离线降级退避：断网首次启动时不允许每次页面导航都重打一轮网络。
/// </summary>
public sealed class HeroLocalizationServiceTests : IDisposable
{
    private static readonly ILogger SilentLog = new LoggerConfiguration().CreateLogger();

    private readonly string _dir;

    public HeroLocalizationServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "dotainsight-localization-tests", Guid.NewGuid().ToString("N"));
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

    private sealed class CountingOfflineHandler : HttpMessageHandler
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

    private sealed class StubHttpClientFactory(CountingOfflineHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }

    [Fact]
    public async Task OfflineFirstLaunch_DegradedFallback_DoesNotRetryWithinBackoff()
    {
        var handler = new CountingOfflineHandler();
        var cache = new LiteDbCacheService(SilentLog, Path.Combine(_dir, "cache.db"));
        var service = new HeroLocalizationService(new StubHttpClientFactory(handler), cache, SilentLog);

        // 第一次：尝试网络失败 → 退避期内返回内置表
        var first = await service.GetChineseNamesByIdAsync();
        Assert.Equal(1, handler.Requests);
        Assert.NotEmpty(first);
        Assert.Equal(HeroChineseNames.ById.Count, first.Count);

        // 退避期内反复调用：不再发起网络请求，直接内置表顶住
        for (var i = 0; i < 3; i++)
        {
            var again = await service.GetChineseNamesByIdAsync();
            Assert.Equal(HeroChineseNames.ById.Count, again.Count);
        }

        Assert.Equal(1, handler.Requests);

        // 跨过退避期后：允许再试一次网络
        typeof(HeroLocalizationService)
            .GetField("_degradedUntilUtc", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(service, DateTime.UtcNow.AddSeconds(-1));
        _ = await service.GetChineseNamesByIdAsync();
        Assert.Equal(2, handler.Requests);

        cache.Dispose();
    }
}

/// <summary>
/// 单实例互斥体：第二个进程必须识别为「已有实例」；释放后可再次获取。
/// </summary>
public sealed class SingleInstanceTests
{
    [Fact]
    public void SecondAcquireFails_UntilFirstReleases()
    {
        // 唯一互斥体名：不与正在运行的应用实例撞名（该测试曾因应用开着而假失败）
        var name = "Local\\DotaInsight.Tests." + Guid.NewGuid().ToString("N");
        Assert.True(SingleInstance.TryAcquireFirst(out var first, name));
        try
        {
            // 同会话内的第二个「进程」（此处同进程模拟）
            Assert.False(SingleInstance.TryAcquireFirst(out var second, name));
            Assert.Null(second);
        }
        finally
        {
            first!.Dispose();
        }

        // 释放后可再次成为第一个实例；guard 重复 Dispose 应幂等
        Assert.True(SingleInstance.TryAcquireFirst(out var third, name));
        third!.Dispose();
        third.Dispose();
    }

    [Fact]
    public void TryActivateExisting_NonexistentWindow_ReturnsFalse()
    {
        Assert.False(SingleInstance.TryActivateExisting($"不存在的窗口-{Guid.NewGuid():N}"));
        Assert.False(SingleInstance.TryActivateExisting(string.Empty));
    }
}
