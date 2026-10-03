using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotaInsight.Helpers;
using DotaInsight.Models;
using DotaInsight.Services;
using Serilog;

namespace DotaInsight.ViewModels.Pages;

/// <summary>
/// 比赛详情页 ViewModel：双方阵容与逐人数据。
/// </summary>
public partial class MatchDetailViewModel : ObservableObject, INavigationAware
{
    private readonly IMatchAnalysisService _matchService;
    private readonly INavigationService _navigation;
    private readonly ILogger _logger;
    private CancellationTokenSource? _cts;

    public MatchDetailViewModel(
        IMatchAnalysisService matchService,
        INavigationService navigation,
        ILogger logger)
    {
        _matchService = matchService;
        _navigation = navigation;
        _logger = logger.ForContext<MatchDetailViewModel>();
        Radiant = new ObservableCollection<MatchPlayerItem>();
        Dire = new ObservableCollection<MatchPlayerItem>();
    }

    public ObservableCollection<MatchPlayerItem> Radiant { get; }

    public ObservableCollection<MatchPlayerItem> Dire { get; }

    [ObservableProperty]
    private long matchId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBasic))]
    [NotifyPropertyChangedFor(nameof(HasDetail))]
    [NotifyPropertyChangedFor(nameof(IsUnparsed))]
    [NotifyPropertyChangedFor(nameof(ShowNotice))]
    private MatchDetail? detail;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBasic))]
    [NotifyPropertyChangedFor(nameof(HasDetail))]
    [NotifyPropertyChangedFor(nameof(IsUnparsed))]
    [NotifyPropertyChangedFor(nameof(ShowNotice))]
    private bool isLoading;

    [ObservableProperty]
    private string statusMessage = string.Empty;

    /// <summary>拿到了比赛本身（哪怕逐人数据还没解析）。</summary>
    public bool HasBasic => Detail is not null;

    /// <summary>双方阵容数据齐备。</summary>
    public bool HasDetail => Detail is { HasPlayers: true };

    /// <summary>比赛已拿到但没有逐人数据：显示「尚未解析」提示而不是空表。</summary>
    public bool IsUnparsed => !IsLoading && Detail is { HasPlayers: false };

    /// <summary>既没在加载、也没有阵容数据：显示提示卡（尚未解析或加载失败）。</summary>
    public bool ShowNotice => !IsLoading && !HasDetail;

    public void OnNavigatedTo(object? parameter)
    {
        var id = parameter switch
        {
            long l => l,
            int i => i,
            RecentMatchItem item => item.MatchId,
            string s when long.TryParse(s, out var parsed) => parsed,
            _ => 0L
        };

        if (id <= 0)
        {
            StatusMessage = "缺少比赛 ID";
            return;
        }

        _ = LoadAsync(id);
    }

    [RelayCommand]
    private void GoBack()
        => _navigation.NavigateTo(MainWindowViewModel.MatchAnalysisPageKey);

    /// <summary>点击某名玩家：切到战绩分析页并直接查询他。</summary>
    [RelayCommand]
    private void OpenPlayer(MatchPlayerItem? player)
    {
        if (player is null || player.AccountId <= 0)
        {
            return;
        }

        _navigation.NavigateTo(
            MainWindowViewModel.MatchAnalysisPageKey,
            player.AccountId.ToString());
    }

    private async Task LoadAsync(long id)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        try
        {
            IsLoading = true;
            StatusMessage = "正在加载比赛详情…";

            // 先清空上一场，避免连续查看不同比赛时残留
            Detail = null;
            Radiant.Clear();
            Dire.Clear();

            var detail = await _matchService
                .GetMatchDetailAsync(id, token)
                .ConfigureAwait(true);

            if (token.IsCancellationRequested)
            {
                return;
            }

            // 服务层内部 ConfigureAwait(false)，续体线程不保证是 UI 线程，
            // 而这里要写绑定属性并重建集合，必须回到 UI 线程。
            await OnUiThreadAsync(() =>
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                MatchId = id;

                Radiant.Clear();
                Dire.Clear();
                foreach (var player in detail?.Radiant ?? [])
                {
                    Radiant.Add(player);
                }

                foreach (var player in detail?.Dire ?? [])
                {
                    Dire.Add(player);
                }

                Detail = detail;
                StatusMessage = detail is null
                    ? "未找到该比赛：ID 可能无效，或 OpenDota 暂无这场数据"
                    : detail.HasPlayers
                        ? string.Empty
                        : "该比赛尚未被 Valve 解析，暂时只能看到基础战况";
            });
        }
        catch (Exception ex) when (ex is OperationCanceledException && token.IsCancellationRequested)
        {
            _logger.Debug("比赛详情请求已取消 MatchId={MatchId}", id);
        }
        catch (Exception ex) when (HttpCall.IsUserCancellation(ex, token))
        {
            _logger.Debug("比赛详情请求已取消 MatchId={MatchId}", id);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "加载比赛详情失败 MatchId={MatchId}", id);
            StatusMessage = "加载失败，请稍后重试";
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                IsLoading = false;
            }
        }
    }

    private static Task OnUiThreadAsync(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(action).Task;
    }
}
