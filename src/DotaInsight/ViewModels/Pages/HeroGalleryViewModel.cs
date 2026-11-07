using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotaInsight.Models;
using DotaInsight.Services;
using Serilog;

namespace DotaInsight.ViewModels.Pages;

/// <summary>
/// 英雄图鉴首页：按主属性分组的英雄列表。
/// </summary>
public partial class HeroGalleryViewModel : ObservableObject
{
    private static readonly string[] AttrOrder = ["力量", "敏捷", "智力", "全才"];

    private readonly IHeroCounterService _heroService;
    private readonly INavigationService _navigation;
    private readonly MainWindowViewModel _shell;
    private readonly ILogger _logger;
    private IReadOnlyList<HeroStat> _allHeroes = Array.Empty<HeroStat>();

    public HeroGalleryViewModel(
        IHeroCounterService heroService,
        INavigationService navigation,
        MainWindowViewModel shell,
        ILogger logger)
    {
        _heroService = heroService;
        _navigation = navigation;
        _shell = shell;
        _logger = logger.ForContext<HeroGalleryViewModel>();
        AllHeroes = new ObservableCollection<HeroStat>();
        FilteredHeroes = new ObservableCollection<HeroStat>();
    }

    public ObservableCollection<HeroStat> AllHeroes { get; }

    /// <summary>
    /// 过滤后的扁平英雄列表（主属性顺序 + 中文名排序）；
    /// 分组由页面的 CollectionViewSource 按 PrimaryAttr 完成，保证虚拟化整页生效。
    /// </summary>
    public ObservableCollection<HeroStat> FilteredHeroes { get; } = new();

    [ObservableProperty]
    private string searchText = string.Empty;

    [ObservableProperty]
    private bool isLoading;

    [ObservableProperty]
    private string statusMessage = "加载英雄列表...";

    [ObservableProperty]
    private int heroCount;

    [ObservableProperty]
    private int visibleCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFilterAll))]
    [NotifyPropertyChangedFor(nameof(IsFilterStr))]
    [NotifyPropertyChangedFor(nameof(IsFilterAgi))]
    [NotifyPropertyChangedFor(nameof(IsFilterInt))]
    [NotifyPropertyChangedFor(nameof(IsFilterUni))]
    private string? attrFilter;

    public bool IsFilterAll => string.IsNullOrWhiteSpace(AttrFilter);

    public bool IsFilterStr => AttrFilter == "力量";

    public bool IsFilterAgi => AttrFilter == "敏捷";

    public bool IsFilterInt => AttrFilter == "智力";

    public bool IsFilterUni => AttrFilter == "全才";

    public bool HasNoResults => !IsLoading && HeroCount > 0 && VisibleCount == 0;

    [RelayCommand]
    private async Task InitializeAsync() => await LoadGalleryAsync(forceRefresh: false);

    /// <summary>手动更新图鉴数据：绕过缓存直连 OpenDota 重新拉取 heroStats。</summary>
    [RelayCommand]
    private async Task UpdateDataAsync() => await LoadGalleryAsync(forceRefresh: true);

    private async Task LoadGalleryAsync(bool forceRefresh)
    {
        try
        {
            IsLoading = true;
            StatusMessage = forceRefresh ? "正在从 OpenDota 更新英雄数据..." : "正在加载英雄列表...";
            var heroes = await _heroService.GetHeroesAsync(forceRefresh).ConfigureAwait(true);
            _allHeroes = heroes;

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                AllHeroes.Clear();
                foreach (var hero in heroes.OrderBy(h => h.DisplayName, Helpers.HeroDisplayHelper.ChineseNameComparer))
                {
                    AllHeroes.Add(hero);
                }

                HeroCount = heroes.Count;
                ApplyFilter();
                Helpers.HeroImageCache.WarmUp(
                    heroes.Select(h => h.IconUrl),
                    decodeWidth: 160);
            });

            StatusMessage = forceRefresh
                ? $"已更新 {heroes.Count} 名英雄 · {DateTime.Now:HH:mm}"
                : $"已载入 {heroes.Count} 名英雄";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "加载图鉴失败");
            StatusMessage = forceRefresh ? "更新失败，请检查网络后重试" : "加载失败，请稍后刷新";
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(HasNoResults));
        }
    }

    private CancellationTokenSource? _searchDebounceCts;

    /// <summary>
    /// 搜索词防抖：搜索框是逐字符触发（UpdateSourceTrigger=PropertyChanged），
    /// 而 ApplyFilter 会整体重建分组集合并让 127 个磁贴重新布局，每个键都跑一遍太浪费；
    /// 停顿 300ms 后才真正过滤，属性筛选不走防抖（点击频率低、需要即时反馈）。
    /// </summary>
    partial void OnSearchTextChanged(string value)
    {
        _searchDebounceCts?.Cancel();
        _searchDebounceCts?.Dispose();
        _searchDebounceCts = new CancellationTokenSource();
        _ = ApplyFilterAfterDelayAsync(_searchDebounceCts.Token);
    }

    private async Task ApplyFilterAfterDelayAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(300, token).ConfigureAwait(true);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        ApplyFilter();
    }

    partial void OnAttrFilterChanged(string? value) => ApplyFilter();

    [RelayCommand]
    private void SetAttrFilter(string? attr)
    {
        AttrFilter = string.IsNullOrWhiteSpace(attr) || attr == AttrFilter
            ? null
            : attr;
    }

    [RelayCommand]
    private void OpenDetail(HeroStat? hero)
    {
        if (hero is null)
        {
            return;
        }

        _shell.CurrentPageTitle = "英雄详情";
        _navigation.NavigateTo(MainWindowViewModel.HeroDetailPageKey, hero.Id);
    }

    [RelayCommand]
    private async Task RefreshAsync() => await InitializeAsync();

    private void ApplyFilter()
    {
        IEnumerable<HeroStat> query = _allHeroes;

        if (!string.IsNullOrWhiteSpace(AttrFilter))
        {
            query = query.Where(h =>
                h.PrimaryAttr.Equals(AttrFilter, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            query = query.Where(h =>
                h.DisplayName.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
                || h.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
        }

        // 扁平列表：主属性顺序在前（决定 CollectionView 的分组顺序），同属性内按中文名排序
        var filtered = query
            .OrderBy(h => Array.IndexOf(AttrOrder, h.PrimaryAttr))
            .ThenBy(h => h.DisplayName, Helpers.HeroDisplayHelper.ChineseNameComparer)
            .ToList();

        VisibleCount = filtered.Count;

        // 分组本身由页面上的 CollectionViewSource（PrimaryAttr）完成，
        // 这里只负责产出有序的扁平列表，让 VirtualizingWrapPanel 能整页虚拟化
        FilteredHeroes.Clear();
        foreach (var hero in filtered)
        {
            FilteredHeroes.Add(hero);
        }

        UpdateStatusMessage();
        OnPropertyChanged(nameof(HasNoResults));
        WarmUpHeroImages();
    }

    /// <summary>
    /// 图鉴全量预热：后台下载全部英雄渲染图，落盘后在 UI 线程重新生成图块绑定。
    /// HeroImageCache 冷未命中在 UI 线程返回空位（绝不同步等网络），靠这一步补图。
    /// </summary>
    private void WarmUpHeroImages()
    {
        var urls = _allHeroes
            .Select(h => h.IconUrl)
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (urls.Count == 0)
        {
            return;
        }

        _ = Helpers.HeroImageCache.WarmUpAsync(urls, decodeWidth: 160).ContinueWith(
            _ => Helpers.UiDispatcher.OnUiAsync(() => OnPropertyChanged(nameof(FilteredHeroes))),
            TaskScheduler.Default);
    }

    private void UpdateStatusMessage()
    {
        if (HeroCount == 0)
        {
            StatusMessage = "暂无数据，请检查网络后刷新";
            return;
        }

        StatusMessage = VisibleCount == HeroCount
            ? $"{HeroCount} 名英雄"
            : $"显示 {VisibleCount} / {HeroCount}";
    }
}

