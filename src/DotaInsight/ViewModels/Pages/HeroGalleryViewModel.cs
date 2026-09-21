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
        HeroGroups = new ObservableCollection<HeroAttrGroup>();
    }

    public ObservableCollection<HeroStat> AllHeroes { get; }

    public ObservableCollection<HeroAttrGroup> HeroGroups { get; }

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
    private async Task InitializeAsync()
    {
        try
        {
            IsLoading = true;
            StatusMessage = "正在加载英雄列表...";
            var heroes = await _heroService.GetHeroesAsync().ConfigureAwait(true);
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
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "加载图鉴失败");
            StatusMessage = "加载失败，请稍后刷新";
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(HasNoResults));
        }
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

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

        var filtered = query
            .OrderBy(h => h.DisplayName, Helpers.HeroDisplayHelper.ChineseNameComparer)
            .ToList();

        VisibleCount = filtered.Count;

        var attrs = string.IsNullOrWhiteSpace(AttrFilter)
            ? AttrOrder
            : AttrOrder.Where(a => a == AttrFilter);

        HeroGroups.Clear();
        foreach (var attr in attrs)
        {
            var heroes = filtered
                .Where(h => h.PrimaryAttr.Equals(attr, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (heroes.Count == 0)
            {
                continue;
            }

            var group = new HeroAttrGroup(attr, heroes.Count);
            foreach (var hero in heroes)
            {
                group.Heroes.Add(hero);
            }

            HeroGroups.Add(group);
        }

        UpdateStatusMessage();
        OnPropertyChanged(nameof(HasNoResults));
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

/// <summary>
/// 按主属性分组的英雄列表。
/// </summary>
public sealed class HeroAttrGroup
{
    public HeroAttrGroup(string attrName, int count)
    {
        AttrName = attrName;
        Count = count;
        Heroes = new ObservableCollection<HeroStat>();
    }

    public string AttrName { get; }

    public int Count { get; }

    public string HeaderText => $"{AttrName}  ·  {Count}";

    public ObservableCollection<HeroStat> Heroes { get; }
}

