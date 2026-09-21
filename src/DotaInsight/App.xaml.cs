using System.IO;
using System.Net.Http;
using System.Windows;
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

        Helpers.AppPaths.EnsureCreated();
        Helpers.HeroImageCache.TryRemoveLegacyInstallCache();

        ConfigureLogging();
        Log.Information("DotaInsight 启动，数据目录：{Root}", Helpers.AppPaths.Root);

        var services = new ServiceCollection();
        ConfigureServices(services);
        _serviceProvider = services.BuildServiceProvider();

        // 先按系统主题上色，再创建主窗口
        var theme = _serviceProvider.GetRequiredService<IThemeService>();
        theme.ApplyFollowSystem();
        Log.Information("启动主题：{Theme}（追随系统）", theme.IsDark ? "暗黑" : "明亮");

        var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("DotaInsight 退出");
        Log.CloseAndFlush();
        _serviceProvider?.Dispose();
        base.OnExit(e);
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
            return nav;
        });

        services.AddHttpClient<IHeroLocalizationService, HeroLocalizationService>(client =>
            {
                client.Timeout = TimeSpan.FromSeconds(15);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("DotaInsight/1.0");
            })
            .AddPolicyHandler(GetRetryPolicy());

        services.AddHttpClient<IHeroProfileService, HeroProfileService>(client =>
            {
                client.Timeout = TimeSpan.FromSeconds(20);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("DotaInsight/1.0");
            })
            .AddPolicyHandler(GetRetryPolicy());

        services.AddHttpClient<IHeroCounterService, HeroCounterService>(client =>
            {
                client.BaseAddress = new Uri("https://api.opendota.com/");
                client.Timeout = TimeSpan.FromSeconds(15);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("DotaInsight/1.0");
            })
            .AddPolicyHandler(GetRetryPolicy());

        services.AddHttpClient<IMatchAnalysisService, MatchAnalysisService>(client =>
            {
                client.BaseAddress = new Uri("https://api.opendota.com/");
                client.Timeout = TimeSpan.FromSeconds(15);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("DotaInsight/1.0");
            })
            .AddPolicyHandler(GetRetryPolicy());

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
                sleepDurationProvider: attempt => TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt)),
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

