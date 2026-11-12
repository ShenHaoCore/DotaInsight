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
            nav.Register(MainWindowViewModel.MatchDetailPageKey, typeof(MatchDetailPage));
            return nav;
        });

        services.AddHttpClient(HeroProfileService.HttpClientName, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(20);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("DotaInsight/1.0");
            })
            .AddPolicyHandler(GetRetryPolicy());

        services.AddHttpClient(HeroCounterService.HttpClientName, client =>
            {
                client.BaseAddress = new Uri(Helpers.ApiEndpoints.OpenDotaBase);
                client.Timeout = TimeSpan.FromSeconds(15);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("DotaInsight/1.0");
            })
            .AddPolicyHandler(GetRetryPolicy());

        // GitHub Releases：升级检查。GitHub 强制要求 User-Agent，缺了直接 403。
        services.AddHttpClient(UpdateService.HttpClientName, UpdateService.ConfigureHttpClient)
            .AddPolicyHandler(GetRetryPolicy());

        // Steam 头像框：抓 steamcommunity 资料页。部分地区对该域名是「间歇放行」而非全断
        // （实测同机多次连接被重置、偶发成功）——重试能显著提高穿透率；
        // 被重置是瞬时失败，短间隔重试成本极低。整体失败由服务静默降级为无框。
        services.AddHttpClient(SteamAvatarFrameService.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(8);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("DotaInsight/1.0");
        })
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
        services.AddSingleton<ISteamAvatarFrameService, SteamAvatarFrameService>();

        services.AddSingleton<HomeViewModel>();
        services.AddSingleton<HomePage>();
        services.AddSingleton<HeroGalleryViewModel>();
        services.AddSingleton<HeroGalleryPage>();
        services.AddSingleton<HeroDetailViewModel>();
        services.AddSingleton<HeroDetailPage>();
        services.AddSingleton<MatchAnalysisViewModel>();
        services.AddSingleton<MatchAnalysisPage>();
        services.AddSingleton<MatchDetailViewModel>();
        services.AddSingleton<MatchDetailPage>();
        services.AddSingleton<MainWindowViewModel>();

        // 清理缓存弹窗：每次打开都要一份新的状态（上次的进度/结果不能残留）
        services.AddTransient<ClearCacheDialogViewModel>();
        services.AddTransient<ClearCacheDialogWindow>();

        services.AddTransient<MainWindow>();
    }

    private static IAsyncPolicy<HttpResponseMessage> GetRetryPolicy()
    {
        return HttpPolicyExtensions
            .HandleTransientHttpError()
            .OrResult(r => (int)r.StatusCode == 429)
            .WaitAndRetryAsync(
                retryCount: 2,
                // 约 2s、4s，给 OpenDota 限流留恢复窗口
                sleepDurationProvider: attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)),
                onRetry: (outcome, delay, attempt, _) =>
                {
                    Log.Warning(
                        "HttpClient 重试 #{Attempt}，延迟 {Delay}ms，原因：{Reason}",
                        attempt,
                        delay.TotalMilliseconds,
                        outcome.Exception?.Message ?? outcome.Result?.StatusCode.ToString());
                });
    }
}
