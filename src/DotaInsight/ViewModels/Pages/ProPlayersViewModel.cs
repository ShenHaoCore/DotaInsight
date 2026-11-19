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
    private readonly MainWindowViewModel _mainWindow;
    private readonly INetworkGate _networkGate;
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
        MainWindowViewModel mainWindow,
        INetworkGate networkGate,
        ILogger logger)
    {
        _proSceneService = proSceneService;
        _mainWindow = mainWindow;
        _networkGate = networkGate;
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
        // 用户主动加载进行中：后台预热让路（_cts 初始化不会抛，异常由下方原 try/catch 全兜）
        var gateOp = _networkGate.BeginUserOperation();
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        try
        {
            IsLoading = true;
            StatusMessage = forceRefresh ? "正在刷新职业选手榜单…" : "正在加载职业选手榜单…";

            var data = await _proSceneService.GetProSceneAsync(
                forceRefresh,
                token,
                onBackgroundRefreshed: OnProSceneRefreshed).ConfigureAwait(true);
            if (token.IsCancellationRequested)
            {
                return;
            }

            ApplyProSceneData(data);
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
            gateOp.Dispose();
        }
    }

    /// <summary>把一次榜单数据应用到界面（首次加载与后台刷新共用）。</summary>
    private void ApplyProSceneData(ProSceneData data)
    {
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
        WarmAvatars();

        StatusMessage = data.RefreshingInBackground
            ? "已展示上次榜单（数据可能略旧），后台更新中…"
            : string.Empty;
    }

    /// <summary>后台刷新完成回调（后台线程）：换新榜或提示失败。</summary>
    private void OnProSceneRefreshed(ProSceneData? fresh)
    {
        _ = System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            if (fresh is null)
            {
                StatusMessage = "榜单后台更新失败（展示的是上次榜单）";
                return;
            }

            _logger.Information("职业选手榜单后台刷新结果已应用");
            ApplyProSceneData(fresh);
            StatusMessage = "榜单已更新为最新";
        });
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync(true);

    /// <summary>点击选手行 → 战绩分析页查询该选手。</summary>
    [RelayCommand]
    private void OpenPlayer(ProPlayerRow? row)
    {
        if (row is null)
        {
            return;
        }

        // 直接把账号作为导航参数带上：战绩页 OnNavigatedTo 参数分支会立即查询，
        // 不会再先跑一次「默认账号」查询再被取消。
        _mainWindow.NavigateMatchAnalysisCommand.Execute(row.AccountId.ToString());
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
