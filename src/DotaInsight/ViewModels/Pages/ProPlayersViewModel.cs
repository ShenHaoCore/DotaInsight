using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotaInsight.Helpers;
using DotaInsight.Models;
using DotaInsight.Services;
using Serilog;
using Wpf.Ui.Controls;

namespace DotaInsight.ViewModels.Pages;

/// <summary>
/// 职业选手榜单页：近期赛场选手榜（出场/胜负聚合）+ 职业战队榜（OpenDota rating）。
/// 点击选手行跳转战绩分析页查询该选手。
/// </summary>
public partial class ProPlayersViewModel : ObservableObject, INavigationAware
{
    private readonly IProSceneService _proSceneService;
    private readonly MatchAnalysisViewModel _matchAnalysis;
    private readonly MainWindowViewModel _mainWindow;
    private readonly ILogger _logger;
    private CancellationTokenSource? _cts;
    private ProSceneData? _data;

    [ObservableProperty]
    private bool isLoading;

    [ObservableProperty]
    private string statusMessage = string.Empty;

    [ObservableProperty]
    private int topTabIndex;

    [ObservableProperty]
    private string aggregatedText = string.Empty;

    public ObservableCollection<ProPlayerRow> Rows { get; } = new();

    public ObservableCollection<ProTeamRow> Teams { get; } = new();

    public bool HasData => Rows.Count > 0;

    public bool HasTeams => Teams.Count > 0;

    public ProPlayersViewModel(
        IProSceneService proSceneService,
        MatchAnalysisViewModel matchAnalysis,
        MainWindowViewModel mainWindow,
        ILogger logger)
    {
        _proSceneService = proSceneService;
        _matchAnalysis = matchAnalysis;
        _mainWindow = mainWindow;
        _logger = logger.ForContext<ProPlayersViewModel>();

        // 头像预热完成或集合变化时刷新可见性标志
        Rows.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasData));
        };
        Teams.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasTeams));
        };
    }

    public void OnNavigatedTo(object? parameter)
    {
        _ = LoadAsync();
    }

    public void OnNavigatedFrom()
    {
    }

    [RelayCommand]
    private async Task LoadAsync(bool forceRefresh = false)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        try
        {
            IsLoading = true;
            StatusMessage = forceRefresh ? "正在刷新职业选手榜单…" : "正在加载职业选手榜单…";

            var data = await _proSceneService.GetProSceneAsync(token).ConfigureAwait(true);
            if (token.IsCancellationRequested)
            {
                return;
            }

            _data = data;
            Rows.Clear();
            foreach (var row in data.PlayerRows)
            {
                Rows.Add(row);
            }

            Teams.Clear();
            foreach (var team in data.TeamRows)
            {
                Teams.Add(team);
            }

            var local = data.AggregatedUtc.ToLocalTime();
            AggregatedText = $"聚合于 {local:MM-dd HH:mm} · 数据源 OpenDota";
            StatusMessage = string.Empty;
            WarmAvatars();
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "职业选手榜单加载失败");
            StatusMessage = $"职业选手榜单加载失败：{ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync(true);

    /// <summary>点击选手行 → 战绩分析页查询该选手。</summary>
    [RelayCommand]
    private async Task OpenPlayerAsync(ProPlayerRow? row)
    {
        if (row is null)
        {
            return;
        }

        // 先切页面（VM 单例共享，查询立即开始），加载态在战绩页展示
        _mainWindow.NavigateMatchAnalysisCommand.Execute(null);
        await _matchAnalysis.QueryExternalAsync(row.AccountId).ConfigureAwait(true);
    }

    /// <summary>
    /// 选手头像预热：榜单行数有限。冷路径绑定先拿到 null，落盘后重建 Rows 集合
    /// （引用相同的集合不会触发行重绘），行绑定重新求值即命中内存缓存出图。
    /// </summary>
    private void WarmAvatars()
    {
        var urls = Rows
            .Select(r => r.AvatarUrl)
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (urls.Count == 0)
        {
            return;
        }

        _ = Helpers.HeroImageCache.WarmUpAsync(urls, decodeWidth: 64).ContinueWith(
            _ => Helpers.UiDispatcher.OnUiAsync(() =>
            {
                var data = _data;
                if (data is null)
                {
                    return;
                }

                Rows.Clear();
                foreach (var row in data.PlayerRows)
                {
                    Rows.Add(row);
                }
            }),
            TaskScheduler.Default);
    }
}
