using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using DotaInsight.Services;
using DotaInsight.ViewModels;
using DotaInsight.ViewModels.Dialogs;
using DotaInsight.ViewModels.Pages;
using DotaInsight.Views.Dialogs;
using DotaInsight.Views.Pages;
using Microsoft.Extensions.DependencyInjection;
using Polly;
using Polly.Extensions.Http;
using Polly.Timeout;
using Serilog;
using Serilog.Exceptions;

namespace DotaInsight;

/// <summary>
/// 应用程序入口：配置 Serilog、DI、主题。
/// </summary>
public partial class App : Application
{
    private ServiceProvider? _serviceProvider;
    private IDisposable? _singleInstanceGuard;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

        Helpers.AppPaths.EnsureCreated();
        Helpers.HeroImageCache.TryRemoveLegacyInstallCache();

        ConfigureLogging();
        Log.Information("DotaInsight 启动，数据目录：{Root}", Helpers.AppPaths.Root);

        // 单实例保护：第二个进程激活已有窗口后退出。
        // 双实例时 LiteDB 能扛（Connection=shared），但 WebView2 用户数据目录是排他锁，
        // 第二个实例的英雄视频区会静默失效。互斥体在 UI 线程获取、OnExit（同线程）释放。
        if (!Helpers.SingleInstance.TryAcquireFirst(out _singleInstanceGuard))
        {
            Log.Information("检测到已有实例，激活主窗口后退出");
            Helpers.SingleInstance.TryActivateExisting(ViewModels.MainWindowViewModel.WindowTitle);
            Shutdown();
            return;
        }

        var services = new ServiceCollection();
        ConfigureServices(services);
        _serviceProvider = services.BuildServiceProvider();

        var theme = _serviceProvider.GetRequiredService<IThemeService>();
        theme.ApplyStartupTheme();
        Log.Information("启动主题：{Theme}", theme.IsDark ? "暗黑" : "明亮");

        var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        mainWindow.Show();
        // 主窗口已创建后再套一次，确保窗口级主题生效
        theme.ApplyTheme(theme.IsDark);

        // 启动静默检查更新：有新版才弹窗，失败不打扰（Release 构建才启用）
        _serviceProvider.GetRequiredService<IUpdateService>().StartStartupCheck();

        // 本地 Steam 账号导入：纯本地文件检测、零网络，先于预热完成，
        // 首次启动的用户一进战绩页就能按「默认加载当前账号」自动查询自己
        _serviceProvider.GetRequiredService<ISteamAccountImportService>().Start();

        // 后台预热：启动后逐个补缓存（一次一件、间隔拉开、用户操作时让路）
        _serviceProvider.GetRequiredService<IBackgroundWarmupService>().Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("DotaInsight 退出");
        if (_serviceProvider?.GetService<IThemeService>() is IDisposable disposableTheme)
        {
            disposableTheme.Dispose();
        }

        Log.CloseAndFlush();
        _serviceProvider?.Dispose();
        _singleInstanceGuard?.Dispose();
        base.OnExit(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "未处理的 UI 异常");
        e.Handled = true;
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            Log.Fatal(ex, "未处理的致命异常 IsTerminating={Terminating}", e.IsTerminating);
        }
    }

    private static void ConfigureLogging()
    {
        var logDir = Helpers.AppPaths.Logs;
        Directory.CreateDirectory(logDir);

        Log.Logger = new LoggerConfiguration()
#if DEBUG
            .MinimumLevel.Debug()
#else
            // Release 收口到 Information：Debug 级日志（逐条缓存命中/写入）对用户排障无必要，
            // 却会让 Async sink 在每次页面导航时写几十条磁盘日志。
            .MinimumLevel.Information()
#endif
            .Enrich.WithExceptionDetails()
            .Enrich.FromLogContext()
            .WriteTo.Async(a => a.File(
                Path.Combine(logDir, "dotainsight-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true))
            .CreateLogger();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton(Log.Logger);

        services.AddSingleton<ILiteDbCacheService, LiteDbCacheService>();
        services.AddSingleton<IAccountService, AccountService>();
        services.AddSingleton<IAppCacheService, AppCacheService>();
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<NavigationService>();
        services.AddSingleton<INavigationService>(sp =>
        {
            var nav = sp.GetRequiredService<NavigationService>();
            nav.Register(MainWindowViewModel.HomePageKey, typeof(HomePage));
            nav.Register(MainWindowViewModel.HeroGalleryPageKey, typeof(HeroGalleryPage));
            nav.Register(MainWindowViewModel.HeroDetailPageKey, typeof(HeroDetailPage));
            nav.Register(MainWindowViewModel.MatchAnalysisPageKey, typeof(MatchAnalysisPage));
            nav.Register(MainWindowViewModel.ProPlayersPageKey, typeof(ProPlayersPage));
            nav.Register(MainWindowViewModel.MatchDetailPageKey, typeof(MatchDetailPage));
            return nav;
        });

        // 网络设置：所有出站 API 走 ProxySwitchHandler 主通道，路由优先级 手动 > 自动检测 > 直连
        services.AddSingleton<INetworkSettingsService, NetworkSettingsService>();
        services.AddSingleton<IAutoProxyService, AutoProxyService>();
        services.AddSingleton<INetworkGate, NetworkGate>();
        services.AddSingleton<ISteamAccountDetector, SteamAccountDetector>();
        services.AddSingleton<ISteamAccountImportService, SteamAccountImportService>();
        services.AddSingleton<IBackgroundWarmupService, BackgroundWarmupService>();

        services.AddHttpClient(HeroProfileService.HttpClientName, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(20);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("DotaInsight/1.0");
            })
            .ConfigurePrimaryHttpMessageHandler(sp => new ProxySwitchHandler(
                sp.GetRequiredService<INetworkSettingsService>(),
                sp.GetRequiredService<IAutoProxyService>()))
            .AddPolicyHandler(GetRetryPolicy());

        services.AddHttpClient(HeroCounterService.HttpClientName, client =>
            {
                client.BaseAddress = new Uri(Helpers.ApiEndpoints.OpenDotaBase);
                // 整链安全网：真实单发超时由内层 Polly 超时策略控制（见 GetOpendotaResiliencePolicy），
                // 预算 = 3 发 × 20s + 退避 5/10s ≈ 75s，安全网放宽到 100s 只做兜底
                client.Timeout = TimeSpan.FromSeconds(100);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("DotaInsight/1.0");
            })
            .ConfigurePrimaryHttpMessageHandler(sp => new ProxySwitchHandler(
                sp.GetRequiredService<INetworkSettingsService>(),
                sp.GetRequiredService<IAutoProxyService>()))
            .AddPolicyHandler(GetOpendotaResiliencePolicy());

        // GitHub Releases：升级检查。GitHub 强制要求 User-Agent，缺了直接 403。
        services.AddHttpClient(UpdateService.HttpClientName, UpdateService.ConfigureHttpClient)
            .ConfigurePrimaryHttpMessageHandler(sp => new ProxySwitchHandler(
                sp.GetRequiredService<INetworkSettingsService>(),
                sp.GetRequiredService<IAutoProxyService>()))
            .AddPolicyHandler(GetRetryPolicy());

        // Steam 头像框：抓 steamcommunity 资料页。部分地区对该域名是「间歇放行」而非全断
        // （实测同机多次连接被重置、偶发成功）——重试能显著提高穿透率；
        // 被重置是瞬时失败，短间隔重试成本极低。整体失败由服务静默降级为无框。
        services.AddHttpClient(SteamAvatarFrameService.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(8);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("DotaInsight/1.0");
        })
        .ConfigurePrimaryHttpMessageHandler(sp => new ProxySwitchHandler(
            sp.GetRequiredService<INetworkSettingsService>(),
            sp.GetRequiredService<IAutoProxyService>()))
        .AddPolicyHandler(HttpPolicyExtensions
            .HandleTransientHttpError()
            .OrResult(r => (int)r.StatusCode == 429)
            .WaitAndRetryAsync(
                retryCount: 2,
                sleepDurationProvider: _ => TimeSpan.FromMilliseconds(600)));

        services.AddSingleton<IUpdateService, UpdateService>();
        services.AddSingleton<IDialogService, DialogService>();

        services.AddSingleton<IHeroLocalizationService, HeroLocalizationService>();
        services.AddSingleton<IHeroProfileService, HeroProfileService>();
        services.AddSingleton<IHeroCounterService, HeroCounterService>();
        services.AddSingleton<IItemCatalogService, ItemCatalogService>();
        services.AddSingleton<IMatchAnalysisService, MatchAnalysisService>();
        services.AddSingleton<IProSceneService, ProSceneService>();
        services.AddSingleton<ISteamAvatarFrameService, SteamAvatarFrameService>();

        services.AddSingleton<HomeViewModel>();
        services.AddSingleton<HomePage>();
        services.AddSingleton<HeroGalleryViewModel>();
        services.AddSingleton<HeroGalleryPage>();
        services.AddSingleton<HeroDetailViewModel>();
        services.AddSingleton<HeroDetailPage>();
        services.AddSingleton<MatchAnalysisViewModel>();
        services.AddSingleton<MatchAnalysisPage>();
        services.AddSingleton<ProPlayersViewModel>();
        services.AddSingleton<ProPlayersPage>();
        services.AddSingleton<MatchDetailViewModel>();
        services.AddSingleton<MatchDetailPage>();
        services.AddSingleton<MainWindowViewModel>();

        // 清理缓存弹窗：每次打开都要一份新的状态（上次的进度/结果不能残留）
        services.AddTransient<ClearCacheDialogViewModel>();
        services.AddTransient<ClearCacheDialogWindow>();

        // 网络设置弹窗：同上，输入框从当前设置初始化，保存即广播
        services.AddTransient<NetworkSettingsDialogViewModel>();
        services.AddTransient<NetworkSettingsDialogWindow>();

        services.AddTransient<MainWindow>();
    }

    private static IAsyncPolicy<HttpResponseMessage> GetRetryPolicy(
        int retryCount = 2,
        Func<int, TimeSpan>? sleep = null)
    {
        sleep ??= attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt));
        return HttpPolicyExtensions
            .HandleTransientHttpError()
            // 内层 Polly 超时策略以 TimeoutRejectedException 拒绝，
            // 不属于 TransientHttpError；不重试的话线路抖动时一发就死。
            // 注意：它不继承 TimeoutException（基类是 Exception），必须按具体类型匹配；
            // 也不能靠匹配 HttpClient 超时的 TaskCanceledException——Polly 在管道内部，
            // 看到的只有套接字层的 TaskCanceledException→IOException 链，
            // TimeoutException 包装是 HttpClient 在管道外加的，到不了重试过滤器。
            // 用户主动取消走 OperationCanceledException，不会误重试。
            .Or<TimeoutRejectedException>()
            .OrResult(r => (int)r.StatusCode == 429)
            .WaitAndRetryAsync(
                retryCount: retryCount,
                sleepDurationProvider: sleep,
                onRetry: (outcome, delay, attempt, _) =>
                {
                    Log.Warning(
                        "HttpClient 重试 #{Attempt}，延迟 {Delay}ms，原因：{Reason}",
                        attempt,
                        delay.TotalMilliseconds,
                        outcome.Exception?.Message ?? outcome.Result?.StatusCode.ToString());
                });
    }

    /// <summary>
    /// OpenDota 专用韧性：外层重试 × 内层单发超时。
    /// 单发超时必须放在 Polly 链内层——client.Timeout 是整条 handler 链的预算，
    /// 第 1 发超时就耗光预算，重试第 2 发被已取消的令牌秒杀（表现为从不重试）。
    /// 且 Polly 在管道内部只能看到套接字层的异常形状，靠它识别 HttpClient 超时不可靠；
    /// 内层超时策略由 Polly 自己抛 TimeoutRejectedException，形状确定。
    ///
    /// 预算依据：高峰期实测单发可到 12~25s（/players/{id}/wl 甚至 >25s），
    /// 8s 单发会让每一发都必然超时、重试再多也等不到一次成功。
    /// 3 发 × 20s、间隔 5/10s（总 ~75s），既覆盖慢线路又不至于让用户干等太久；
    /// 期间用户点其他选手会取消本轮。
    /// </summary>
    private static IAsyncPolicy<HttpResponseMessage> GetOpendotaResiliencePolicy()
        => Policy.WrapAsync(
            GetRetryPolicy(
                retryCount: 2,
                sleep: attempt => TimeSpan.FromSeconds(5 * attempt)),
            Policy.TimeoutAsync<HttpResponseMessage>(TimeSpan.FromSeconds(20)));
}
