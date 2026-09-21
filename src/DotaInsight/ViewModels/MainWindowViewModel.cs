using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    private readonly INavigationService _navigationService;
    private readonly IThemeService _themeService;
    private readonly IAppCacheService _cacheService;
    private readonly ILogger _logger;

    public MainWindowViewModel(
        INavigationService navigationService,
        IThemeService themeService,
        IAppCacheService cacheService,
        ILogger logger)
    {
        _navigationService = navigationService;
        _themeService = themeService;
        _cacheService = cacheService;
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

    public SymbolIcon ThemeToggleIcon
        => new()
        {
            Symbol = ThemeToggleSymbol
        };

    public SymbolRegular ThemeToggleSymbol
        => IsDarkTheme ? SymbolRegular.WeatherSunny24 : SymbolRegular.WeatherMoon24;

    public string ThemeToggleTooltip
        => IsDarkTheme ? "切换到明亮主题" : "切换到暗黑主题";

    public string ClearCacheTooltip => $"清理本地缓存\n{CacheInfoText}";

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
}

