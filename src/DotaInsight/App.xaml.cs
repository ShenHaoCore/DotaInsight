using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using DotaInsight.Services;
using DotaInsight.ViewModels;
using DotaInsight.ViewModels.Pages;
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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

        Helpers.AppPaths.EnsureCreated();
        Helpers.HeroImageCache.TryRemoveLegacyInstallCache();

        ConfigureLogging();
        Log.Information("DotaInsight 启动，数据目录：{Root}", Helpers.AppPaths.Root);

        var services = new ServiceCollection();
        ConfigureServices(services);
        _serviceProvider = services.BuildServiceProvider();

        var theme = _serviceProvider.GetRequiredService<IThemeService>();
        theme.ApplyFollowSystem();
        Log.Information("启动主题：{Theme}（追随系统）", theme.IsDark ? "暗黑" : "明亮");

        var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        mainWindow.Show();
        // 主窗口已创建后再套一次，确保窗口级主题生效
        theme.ApplyTheme(theme.IsDark);
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
            .MinimumLevel.Debug()
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
            nav.Register(MainWindowViewModel.HeroCounterPageKey, typeof(HeroCounterPage));
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
                client.BaseAddress = new Uri("https://api.opendota.com/");
                client.Timeout = TimeSpan.FromSeconds(15);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("DotaInsight/1.0");
            })
            .AddPolicyHandler(GetRetryPolicy());

        services.AddSingleton<IHeroLocalizationService, HeroLocalizationService>();
        services.AddSingleton<IHeroProfileService, HeroProfileService>();
        services.AddSingleton<IHeroCounterService, HeroCounterService>();
        services.AddSingleton<IItemCatalogService, ItemCatalogService>();
        services.AddSingleton<IMatchAnalysisService, MatchAnalysisService>();

        services.AddSingleton<HomeViewModel>();
        services.AddSingleton<HomePage>();
        services.AddSingleton<HeroGalleryViewModel>();
        services.AddSingleton<HeroGalleryPage>();
        services.AddSingleton<HeroDetailViewModel>();
        services.AddSingleton<HeroDetailPage>();
        services.AddSingleton<HeroCounterViewModel>();
        services.AddSingleton<HeroCounterPage>();
        services.AddSingleton<MatchAnalysisViewModel>();
        services.AddSingleton<MatchAnalysisPage>();
        services.AddSingleton<MatchDetailViewModel>();
        services.AddSingleton<MatchDetailPage>();
        services.AddSingleton<MainWindowViewModel>();
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
