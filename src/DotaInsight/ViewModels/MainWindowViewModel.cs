using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotaInsight.Helpers;
using DotaInsight.Services;
using Serilog;
using Wpf.Ui.Controls;

namespace DotaInsight.ViewModels;

/// <summary>
/// 主窗口 ViewModel：导航、主题与缓存清理。
/// </summary>
public partial class MainWindowViewModel : ObservableObject
{
    public const string HomePageKey = "Home";
    public const string HeroGalleryPageKey = "HeroGallery";
    public const string HeroDetailPageKey = "HeroDetail";
    public const string HeroCounterPageKey = "HeroCounter";
    public const string MatchAnalysisPageKey = "MatchAnalysis";

    /// <summary>比赛详情是战绩分析的下级页面，不在侧边栏导航里。</summary>
    public const string MatchDetailPageKey = "MatchDetail";

    private readonly INavigationService _navigationService;
    private readonly IThemeService _themeService;
    private readonly IAppCacheService _cacheService;
    private readonly IUpdateService _updateService;
    private readonly ILogger _logger;

    public MainWindowViewModel(
        INavigationService navigationService,
        IThemeService themeService,
        IAppCacheService cacheService,
        IUpdateService updateService,
        ILogger logger)
    {
        _navigationService = navigationService;
        _themeService = themeService;
        _cacheService = cacheService;
        _updateService = updateService;
        _logger = logger.ForContext<MainWindowViewModel>();
        IsDarkTheme = _themeService.IsDark;
        RefreshCacheInfo();
    }

    [ObservableProperty]
    private string title = "DotaInsight - Dota2 赛事数据分析";

    [ObservableProperty]
    private string currentPageTitle = "首页";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThemeToggleIcon))]
    [NotifyPropertyChangedFor(nameof(ThemeToggleSymbol))]
    [NotifyPropertyChangedFor(nameof(ThemeToggleTooltip))]
    private bool isDarkTheme;

    [ObservableProperty]
    private string activeNav = HomePageKey;

    [ObservableProperty]
    private string cacheInfoText = "缓存统计中...";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateTooltip))]
    private bool isCheckingUpdate;

    private SymbolIcon? _themeToggleIcon;

    /// <summary>主题图标：按需创建并复用同一实例，仅主题切换时重建（不在 getter 里每次 new 控件）。</summary>
    public SymbolIcon ThemeToggleIcon
    {
        get
        {
            if (_themeToggleIcon is null || _themeToggleIcon.Symbol != ThemeToggleSymbol)
            {
                _themeToggleIcon = new SymbolIcon { Symbol = ThemeToggleSymbol };
            }

            return _themeToggleIcon;
        }
    }

    public SymbolRegular ThemeToggleSymbol
        => IsDarkTheme ? SymbolRegular.WeatherSunny24 : SymbolRegular.WeatherMoon24;

    public string ThemeToggleTooltip
        => IsDarkTheme ? "切换到明亮主题" : "切换到暗黑主题";

    public string ClearCacheTooltip => $"清理本地缓存\n{CacheInfoText}";

    public string UpdateTooltip
        => IsCheckingUpdate ? "正在检查更新…" : $"检查更新（当前 {_updateService.CurrentVersionText}）";

    [RelayCommand]
    private void NavigateHome()
    {
        CurrentPageTitle = "首页";
        ActiveNav = HomePageKey;
        _navigationService.NavigateTo(HomePageKey);
    }

    [RelayCommand]
    private void NavigateGallery()
    {
        CurrentPageTitle = "英雄图鉴";
        ActiveNav = HeroGalleryPageKey;
        _navigationService.NavigateTo(HeroGalleryPageKey);
    }

    [RelayCommand]
    private void NavigateCounter()
    {
        CurrentPageTitle = "克制分析";
        ActiveNav = HeroCounterPageKey;
        _navigationService.NavigateTo(HeroCounterPageKey);
    }

    [RelayCommand]
    private void NavigateMatchAnalysis()
    {
        CurrentPageTitle = "战绩分析";
        ActiveNav = MatchAnalysisPageKey;
        _navigationService.NavigateTo(MatchAnalysisPageKey);
    }

    [RelayCommand]
    private void ToggleTheme()
    {
        _themeService.Toggle();
        IsDarkTheme = _themeService.IsDark;
        _logger.Information("切换主题：{Theme}", IsDarkTheme ? "暗黑" : "明亮");
    }

    [RelayCommand]
    private void RefreshCacheInfo()
    {
        var stats = _cacheService.GetStats();
        CacheInfoText = stats.SummaryText;
        OnPropertyChanged(nameof(ClearCacheTooltip));
    }

    [RelayCommand]
    private void ClearCache()
    {
        RefreshCacheInfo();
        var confirm = System.Windows.MessageBox.Show(
            $"将删除本地头像与数据缓存：\n{CacheInfoText}\n\n清理后需重新联网加载，是否继续？",
            "清理缓存",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);

        if (confirm != System.Windows.MessageBoxResult.Yes)
        {
            return;
        }

        var result = _cacheService.ClearAll();
        RefreshCacheInfo();
        System.Windows.MessageBox.Show(result.Message, "清理完成", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
    }

    /// <summary>
    /// 手动检查更新：明确告知结论（已是最新 / 有新版 / 失败），有新版时确认后再就地升级。
    /// </summary>
    [RelayCommand]
    private async Task CheckUpdateAsync()
    {
        if (IsCheckingUpdate)
        {
            return;
        }

        IsCheckingUpdate = true;
        try
        {
            var result = await _updateService.CheckAsync();

            switch (result.Status)
            {
                case UpdateCheckStatus.UpToDate:
                    System.Windows.MessageBox.Show(
                        result.Message,
                        "检查更新",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Information);
                    break;

                case UpdateCheckStatus.Failed:
                    System.Windows.MessageBox.Show(
                        result.Message,
                        "检查更新失败",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                    break;

                case UpdateCheckStatus.UpdateAvailable when result.Release is { } release:
                    var confirm = System.Windows.MessageBox.Show(
                        $"{result.Message}\n\n是否现在下载并升级？升级时应用会自动关闭，装好后重新打开。",
                        "发现新版本",
                        System.Windows.MessageBoxButton.YesNo,
                        System.Windows.MessageBoxImage.Question);

                    if (confirm == System.Windows.MessageBoxResult.Yes)
                    {
                        ApplyUpdate(release);
                    }

                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "检查更新时发生异常");
            System.Windows.MessageBox.Show(
                $"检查更新时发生异常：{ex.Message}",
                "检查更新失败",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
        finally
        {
            IsCheckingUpdate = false;
        }
    }

    /// <summary>就地升级；无法自动完成时提供打开发布页面的退路。</summary>
    private void ApplyUpdate(GitHubReleaseInfo release)
    {
        switch (_updateService.Apply(release))
        {
            case UpdateApplyStatus.Started:
                // 下载框已交棒给 ZipExtractor，应用马上退出，无需再提示
                break;

            case UpdateApplyStatus.NotWritable:
                OfferManualDownload(release, "当前程序目录没有写入权限，无法自动升级。\n是否打开发布页面手动下载？", "无法自动升级");
                break;

            case UpdateApplyStatus.Failed:
                OfferManualDownload(release, "升级包下载失败或已取消。\n是否打开发布页面手动下载？", "升级失败");
                break;
        }
    }

    private void OfferManualDownload(GitHubReleaseInfo release, string message, string caption)
    {
        var open = System.Windows.MessageBox.Show(
            message,
            caption,
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);

        if (open == System.Windows.MessageBoxResult.Yes)
        {
            _updateService.OpenReleasePage(release);
        }
    }
}

