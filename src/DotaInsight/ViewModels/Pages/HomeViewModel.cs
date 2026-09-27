using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotaInsight.Models;
using DotaInsight.Services;
using Serilog;

namespace DotaInsight.ViewModels.Pages;

/// <summary>
/// 首页仪表盘：功能入口、玩家快查、版本高胜率快览。
/// </summary>
public partial class HomeViewModel : ObservableObject
{
    private readonly IHeroCounterService _heroService;
    private readonly INavigationService _navigation;
    private readonly IAppCacheService _cacheService;
    private readonly ILiteDbCacheService _liteCache;
    private readonly MainWindowViewModel _shell;
    private readonly ILogger _logger;

    public HomeViewModel(
        IHeroCounterService heroService,
        INavigationService navigation,
        IAppCacheService cacheService,
        ILiteDbCacheService liteCache,
        MainWindowViewModel shell,
        ILogger logger)
    {
        _heroService = heroService;
        _navigation = navigation;
        _cacheService = cacheService;
        _liteCache = liteCache;
        _shell = shell;
        _logger = logger.ForContext<HomeViewModel>();
        TopWinRateHeroes = new ObservableCollection<HeroStat>();
        TopPickRateHeroes = new ObservableCollection<HeroStat>();
        RefreshCacheInfo();
    }

    public ObservableCollection<HeroStat> TopWinRateHeroes { get; }

    public ObservableCollection<HeroStat> TopPickRateHeroes { get; }

    [ObservableProperty]
    private string accountInput = string.Empty;

    [ObservableProperty]
    private bool isLoading;

    [ObservableProperty]
    private string metaStatus = "加载版本数据...";

    [ObservableProperty]
    private string cacheInfoText = "缓存统计中...";

    [RelayCommand]
    private async Task InitializeAsync()
    {
        try
        {
            IsLoading = true;
            MetaStatus = "正在加载版本数据...";
            var heroes = await _heroService.GetHeroesAsync().ConfigureAwait(true);

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                TopWinRateHeroes.Clear();
                foreach (var hero in heroes
                             .Where(h => h.Matches >= 200)
                             .OrderByDescending(h => h.WinRate)
                             .Take(8))
                {
                    TopWinRateHeroes.Add(hero);
                }

                TopPickRateHeroes.Clear();
                foreach (var hero in heroes
                             .OrderByDescending(h => h.PickRate)
                             .Take(8))
                {
                    TopPickRateHeroes.Add(hero);
                }

                MetaStatus = TopWinRateHeroes.Count == 0
                    ? "暂无版本数据"
                    : $"已载入 {heroes.Count} 名英雄 · 展示高胜率 / 高选取";

                Helpers.HeroImageCache.WarmUp(
                    heroes.Select(h => h.IconUrl),
                    decodeWidth: 160);
                RefreshCacheInfo();
                _shell.RefreshCacheInfoCommand.Execute(null);
            });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "首页版本数据加载失败");
            MetaStatus = "版本数据加载失败";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void RefreshCacheInfo()
    {
        CacheInfoText = _cacheService.GetStats().SummaryText;
    }

    [RelayCommand]
    private void ClearCache()
    {
        _shell.ClearCacheCommand.Execute(null);
        RefreshCacheInfo();
    }

    [RelayCommand]
    private void OpenGallery()
    {
        _shell.CurrentPageTitle = "英雄图鉴";
        _shell.ActiveNav = MainWindowViewModel.HeroGalleryPageKey;
        _navigation.NavigateTo(MainWindowViewModel.HeroGalleryPageKey);
    }

    [RelayCommand]
    private void OpenCounter()
    {
        _shell.CurrentPageTitle = "克制分析";
        _shell.ActiveNav = MainWindowViewModel.HeroCounterPageKey;
        _navigation.NavigateTo(MainWindowViewModel.HeroCounterPageKey);
    }

    [RelayCommand]
    private void OpenMatchAnalysis()
    {
        _shell.CurrentPageTitle = "战绩分析";
        _shell.ActiveNav = MainWindowViewModel.MatchAnalysisPageKey;
        _navigation.NavigateTo(MainWindowViewModel.MatchAnalysisPageKey);
    }

    [RelayCommand]
    private void SearchPlayer()
    {
        var input = AccountInput.Trim();
        if (string.IsNullOrWhiteSpace(input))
        {
            OpenMatchAnalysis();
            return;
        }

        SaveHistoryId(input);
        _shell.CurrentPageTitle = "战绩分析";
        _shell.ActiveNav = MainWindowViewModel.MatchAnalysisPageKey;
        _navigation.NavigateTo(MainWindowViewModel.MatchAnalysisPageKey, input);
    }

    private void SaveHistoryId(string accountId)
    {
        var list = _liteCache.Get<List<string>>("match_history_ids") ?? new List<string>();
        list.Remove(accountId);
        list.Insert(0, accountId);
        if (list.Count > 8)
        {
            list.RemoveAt(list.Count - 1);
        }

        _liteCache.Set("match_history_ids", list, TimeSpan.FromDays(30));
    }

    [RelayCommand]
    private void OpenHeroDetail(HeroStat? hero)
    {
        if (hero is null)
        {
            return;
        }

        _shell.CurrentPageTitle = "英雄详情";
        _shell.ActiveNav = MainWindowViewModel.HeroGalleryPageKey;
        _navigation.NavigateTo(MainWindowViewModel.HeroDetailPageKey, hero.Id);
    }

    [RelayCommand]
    private async Task RefreshAsync() => await InitializeAsync();
}

