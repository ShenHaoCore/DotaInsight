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
/// 战绩分析页 ViewModel。
/// </summary>
public partial class MatchAnalysisViewModel : ObservableObject, INavigationAware
{
    private readonly IMatchAnalysisService _matchService;
    private readonly IAccountService _accountService;
    private readonly ILiteDbCacheService _cache;
    private readonly INavigationService _navigation;
    private readonly ILogger _logger;
    private CancellationTokenSource? _cts;

    public MatchAnalysisViewModel(
        IMatchAnalysisService matchService,
        IAccountService accountService,
        ILiteDbCacheService cache,
        INavigationService navigation,
        ILogger logger)
    {
        _matchService = matchService;
        _accountService = accountService;
        _cache = cache;
        _navigation = navigation;
        _logger = logger.ForContext<MatchAnalysisViewModel>();
        Matches = new ObservableCollection<RecentMatchItem>();
        Accounts = new ObservableCollection<SavedAccount>();
        Series = Array.Empty<ISeries>();
        XAxes = [new Axis { Labels = [] }];
        YAxes = [new Axis { MinLimit = 0, MaxLimit = 1.2 }];
    }

    public void OnNavigatedTo(object? parameter)
    {
        LoadAccounts();
        if (parameter is string account && !string.IsNullOrWhiteSpace(account))
        {
            AccountInput = account.Trim();
            if (SearchCommand.CanExecute(null))
            {
                _ = SearchCommand.ExecuteAsync(null);
            }
        }
        // 无参数重复进入：保留上次结果，不重复请求
    }

    public ObservableCollection<RecentMatchItem> Matches { get; }

    /// <summary>已保存的账号（最近使用在前），做成卡片用于一键切换。</summary>
    public ObservableCollection<SavedAccount> Accounts { get; }

    public bool HasAccounts => Accounts.Count > 0;

    [ObservableProperty]
    private string accountInput = string.Empty;

    [ObservableProperty]
    private bool isLoading;

    [ObservableProperty]
    private string statusMessage = "输入 Steam 账号 ID 或 SteamID64 查询近期战绩";

    [ObservableProperty]
    private bool hasData;

    [ObservableProperty]
    private PlayerProfile? profile;

    [ObservableProperty]
    private string recentSummary = string.Empty;

    [ObservableProperty]
    private ISeries[] series;

    [ObservableProperty]
    private Axis[] xAxes;

    [ObservableProperty]
    private Axis[] yAxes;

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (!_matchService.TryParseAccountId(AccountInput, out var accountId))
        {
            StatusMessage = "请输入有效的 Account ID 或 SteamID64";
            return;
        }

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        try
        {
            IsLoading = true;
            HasData = false;
            StatusMessage = $"正在查询账号 {accountId}...";
            _logger.Information("开始战绩分析 AccountId={AccountId}", accountId);

            var result = await _matchService.AnalyzeAsync(accountId, token).ConfigureAwait(true);
            if (token.IsCancellationRequested)
            {
                return;
            }

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                Profile = result.Profile;

                // 查询成功即把账号信息留在本地，下次可点头像直接切回来
                if (result.Profile is not null)
                {
                    _accountService.SaveOrUpdate(result.Profile);
                    RefreshAccounts();
                }

                Matches.Clear();
                foreach (var match in result.Matches)
                {
                    Matches.Add(match);
                }

                UpdateChart(result.Matches);
                HasData = !result.IsEmpty;

                if (result.IsEmpty)
                {
                    StatusMessage = "暂无战绩数据（账号可能隐藏比赛或 ID 不正确）";
                    RecentSummary = string.Empty;
                }
                else
                {
                    RecentSummary =
                        $"近 {result.Matches.Count} 场：{result.RecentWins}胜 {result.RecentLosses}负 · 胜率 {result.RecentWinRate:F1}% · 场均 KDA {result.AvgKda:F2}";
                    StatusMessage = result.IsOffline
                        ? "离线模式：已展示本地缓存"
                        : result.FromCache
                            ? "已加载缓存战绩"
                            : "分析完成";
                }
            });
        }
        catch (Exception ex) when (ex is OperationCanceledException && token.IsCancellationRequested)
        {
            _logger.Debug("战绩请求已取消");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "战绩分析失败");
            StatusMessage = "查询失败，请检查网络或账号 ID";
            HasData = false;
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                IsLoading = false;
            }
        }
    }

    /// <summary>打开展示某场比赛的详情。</summary>
    [RelayCommand]
    private void OpenMatch(RecentMatchItem? match)
    {
        if (match is null || match.MatchId <= 0)
        {
            return;
        }

        _navigation.NavigateTo(MainWindowViewModel.MatchDetailPageKey, match.MatchId);
    }

    /// <summary>点击账号卡片：填入该账号并立即查询。</summary>
    [RelayCommand]
    private async Task SwitchAccountAsync(SavedAccount? account)
    {
        if (account is null)
        {
            return;
        }

        AccountInput = account.AccountIdText;
        await SearchAsync().ConfigureAwait(true);
    }

    /// <summary>把账号卡片从列表里移除。</summary>
    [RelayCommand]
    private void RemoveAccount(SavedAccount? account)
    {
        if (account is null || !_accountService.Remove(account.AccountId))
        {
            return;
        }

        // 移除的正好是当前正在看的账号：清掉结果，避免页面继续显示已删掉的账号
        if (Profile?.AccountId == account.AccountId)
        {
            Profile = null;
            Matches.Clear();
            HasData = false;
            RecentSummary = string.Empty;
            StatusMessage = "已移除该账号，可输入 ID 重新查询";
        }

        RefreshAccounts();
    }

    private void LoadAccounts()
    {
        // 旧版只存了一串纯 ID。这里幂等并入账户列表：卡片先以「账号 xxxxx」占位，
        // 点一次查询就会补上昵称与头像，历史记录不会因为改版而丢失。
        var legacy = _cache.Get<List<string>>(Helpers.CacheKeys.LegacyMatchHistoryIds);
        if (legacy is { Count: > 0 })
        {
            _accountService.MigrateLegacyIds(legacy);
        }

        RefreshAccounts();
    }

    /// <summary>
    /// 重建卡片集合。SavedAccount 不实现属性通知，
    /// 因此「当前选中」高亮靠整体重建来刷新。
    /// </summary>
    private void RefreshAccounts()
    {
        var accounts = _accountService.GetAll();
        Accounts.Clear();
        foreach (var account in accounts)
        {
            Accounts.Add(account);
        }

        OnPropertyChanged(nameof(HasAccounts));
    }

    private void UpdateChart(IReadOnlyList<RecentMatchItem> matches)
    {
        if (matches.Count == 0)
        {
            Series = Array.Empty<ISeries>();
            XAxes = [new Axis { Labels = [] }];
            return;
        }

        // 时间正序展示胜负走势（1=胜，0=负）
        var ordered = matches.OrderBy(m => m.StartTimeLocal).ToList();
        var values = ordered.Select(m => m.IsWin ? 1d : 0d).ToArray();
        var labels = ordered.Select(m => m.StartTimeLocal.ToString("MM-dd")).ToArray();

        Series =
        [
            new ColumnSeries<double>
            {
                Name = "胜负",
                Values = values,
                MaxBarWidth = 18,
                Fill = new SolidColorPaint(SKColor.Parse("#2FD57F"))
            }
        ];

        XAxes =
        [
            new Axis
            {
                Labels = labels,
                LabelsRotation = 20,
                TextSize = 10,
                LabelsPaint = new SolidColorPaint(SKColor.Parse("#8A97A6")),
                SeparatorsPaint = new SolidColorPaint(SKColors.Transparent)
            }
        ];

        YAxes =
        [
            new Axis
            {
                MinLimit = -0.1,
                MaxLimit = 1.2,
                TextSize = 10,
                LabelsPaint = new SolidColorPaint(SKColor.Parse("#8A97A6")),
                SeparatorsPaint = new SolidColorPaint(SKColor.Parse("#232A36"))
            }
        ];
    }
}
