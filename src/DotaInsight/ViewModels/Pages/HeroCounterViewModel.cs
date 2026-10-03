using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotaInsight.Models;
using DotaInsight.Services;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using Serilog;
using SkiaSharp;

namespace DotaInsight.ViewModels.Pages;

/// <summary>
/// 英雄克制分析页（专注对位，从图鉴/详情带入英雄）。
/// </summary>
public partial class HeroCounterViewModel : ObservableObject, INavigationAware
{
    private readonly IHeroCounterService _heroCounterService;
    private readonly INavigationService _navigation;
    private readonly MainWindowViewModel _shell;
    private readonly ILogger _logger;
    private CancellationTokenSource? _loadCts;
    private IReadOnlyList<HeroStat> _allHeroes = Array.Empty<HeroStat>();

    public HeroCounterViewModel(
        IHeroCounterService heroCounterService,
        INavigationService navigation,
        MainWindowViewModel shell,
        ILogger logger)
    {
        _heroCounterService = heroCounterService;
        _navigation = navigation;
        _shell = shell;
        _logger = logger.ForContext<HeroCounterViewModel>();
        AllHeroes = new ObservableCollection<HeroStat>();
        CounteredByList = new ObservableCollection<HeroCounterItem>();
        CountersList = new ObservableCollection<HeroCounterItem>();
        Series = Array.Empty<ISeries>();
        XAxes = [new Axis { Labels = [], LabelsPaint = new SolidColorPaint(SKColor.Parse("#8A97A6")) }];
        YAxes =
        [
            new Axis
            {
                TextSize = 11,
                LabelsPaint = new SolidColorPaint(SKColor.Parse("#8A97A6")),
                SeparatorsPaint = new SolidColorPaint(SKColor.Parse("#232A36"))
            }
        ];
    }

    public ObservableCollection<HeroStat> AllHeroes { get; }

    public ObservableCollection<HeroCounterItem> CounteredByList { get; }

    public ObservableCollection<HeroCounterItem> CountersList { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedHero))]
    private HeroStat? selectedHero;

    [ObservableProperty]
    private string heroSearchText = string.Empty;

    [ObservableProperty]
    private bool isLoading;

    [ObservableProperty]
    private string statusMessage = "从图鉴选择英雄，或在此搜索开始分析";

    [ObservableProperty]
    private bool hasData;

    [ObservableProperty]
    private bool showEmptyPlaceholder;

    [ObservableProperty]
    private ISeries[] series;

    [ObservableProperty]
    private Axis[] xAxes;

    [ObservableProperty]
    private Axis[] yAxes;

    public bool HasSelectedHero => SelectedHero is not null;

    public void OnNavigatedTo(object? parameter)
    {
        // 侧栏重复点入且无新参数：不重新跑加载，避免切换卡顿
        if (parameter is null && _allHeroes.Count > 0)
        {
            return;
        }

        _ = InitializeAndSelectAsync(parameter);
    }

    [RelayCommand]
    private async Task InitializeAsync() => await InitializeAndSelectAsync(null);

    private async Task InitializeAndSelectAsync(object? parameter)
    {
        try
        {
            IsLoading = true;
            if (_allHeroes.Count == 0)
            {
                StatusMessage = "正在加载英雄列表...";
                var heroes = await _heroCounterService.GetHeroesAsync().ConfigureAwait(true);
                _allHeroes = heroes;
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    AllHeroes.Clear();
                    foreach (var hero in heroes)
                    {
                        AllHeroes.Add(hero);
                    }
                });
            }

            int? targetHeroId = parameter switch
            {
                int id => id,
                HeroStat h => h.Id,
                _ => null
            };

            if (targetHeroId is int selectedId)
            {
                var hero = _allHeroes.FirstOrDefault(h => h.Id == selectedId);
                if (hero is not null)
                {
                    await Application.Current.Dispatcher.InvokeAsync(() => SelectedHero = hero);
                    return;
                }
            }

            if (SelectedHero is null)
            {
                StatusMessage = "从图鉴选择英雄，或在此搜索开始分析";
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "克制页初始化失败");
            StatusMessage = "加载失败，请刷新";
        }
        finally
        {
            IsLoading = false;
        }
    }

    partial void OnSelectedHeroChanged(HeroStat? value)
    {
        OnPropertyChanged(nameof(HasSelectedHero));
        if (value is null)
        {
            CounteredByList.Clear();
            CountersList.Clear();
            HasData = false;
            ShowEmptyPlaceholder = false;
            Series = Array.Empty<ISeries>();
            StatusMessage = "从图鉴选择英雄，或在此搜索开始分析";
            return;
        }

        HeroSearchText = value.DisplayName;
        _ = LoadCountersAsync(value);
    }

    [RelayCommand]
    private void OpenGallery()
    {
        _shell.CurrentPageTitle = "英雄图鉴";
        _shell.ActiveNav = MainWindowViewModel.HeroGalleryPageKey;
        _navigation.NavigateTo(MainWindowViewModel.HeroGalleryPageKey);
    }

    [RelayCommand]
    private void OpenDetail()
    {
        if (SelectedHero is null)
        {
            return;
        }

        _shell.CurrentPageTitle = "英雄详情";
        _navigation.NavigateTo(MainWindowViewModel.HeroDetailPageKey, SelectedHero.Id);
    }

    [RelayCommand]
    private void ClearSelection()
    {
        SelectedHero = null;
        HeroSearchText = string.Empty;
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (SelectedHero is null)
        {
            await InitializeAsync();
            return;
        }

        await LoadCountersAsync(SelectedHero);
    }

    private async Task LoadCountersAsync(HeroStat hero)
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
        var token = _loadCts.Token;

        try
        {
            IsLoading = true;
            StatusMessage = $"正在分析 {hero.DisplayName}...";
            ShowEmptyPlaceholder = false;
            HasData = false;

            var result = await _heroCounterService
                .GetCounterRelationsAsync(hero.Id, token)
                .ConfigureAwait(true);

            if (token.IsCancellationRequested)
            {
                return;
            }

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                CounteredByList.Clear();
                foreach (var item in result.CounteredBy.Take(40))
                {
                    CounteredByList.Add(item);
                }

                CountersList.Clear();
                foreach (var item in result.Counters.Take(40))
                {
                    CountersList.Add(item);
                }

                UpdateChart(result);
                HasData = !result.IsEmpty;
                ShowEmptyPlaceholder = result.IsEmpty;
                StatusMessage = result.IsEmpty
                    ? "暂无对位数据"
                    : $"不利 {CounteredByList.Count} · 有利 {CountersList.Count}";
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _logger.Debug("克制请求已取消 HeroId={HeroId}", hero.Id);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "加载克制失败 HeroId={HeroId}", hero.Id);
            StatusMessage = "网络异常，请稍后重试";
            ShowEmptyPlaceholder = true;
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                IsLoading = false;
            }
        }
    }

    private void UpdateChart(HeroCounterResult result)
    {
        var items = result.CounteredBy.Take(6).Reverse()
            .Concat(result.Counters.Take(6))
            .ToList();

        if (items.Count == 0)
        {
            Series = Array.Empty<ISeries>();
            XAxes = [new Axis { Labels = [] }];
            return;
        }

        Series =
        [
            new ColumnSeries<double>
            {
                Name = "胜率差",
                Values = items.Select(x => x.WinRateDiff).ToArray(),
                Fill = new SolidColorPaint(SKColor.Parse("#F03D2E")),
                MaxBarWidth = 26
            }
        ];

        XAxes =
        [
            new Axis
            {
                Labels = items.Select(x => x.HeroName).ToArray(),
                LabelsRotation = 16,
                TextSize = 10,
                LabelsPaint = new SolidColorPaint(SKColor.Parse("#8A97A6")),
                SeparatorsPaint = new SolidColorPaint(SKColors.Transparent)
            }
        ];

        YAxes =
        [
            new Axis
            {
                TextSize = 10,
                LabelsPaint = new SolidColorPaint(SKColor.Parse("#8A97A6")),
                SeparatorsPaint = new SolidColorPaint(SKColor.Parse("#232A36"))
            }
        ];
    }
}
