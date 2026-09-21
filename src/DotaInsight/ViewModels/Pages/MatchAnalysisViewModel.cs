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
    private readonly ILogger _logger;
    private CancellationTokenSource? _cts;

    public MatchAnalysisViewModel(IMatchAnalysisService matchService, ILogger logger)
    {
        _matchService = matchService;
        _logger = logger.ForContext<MatchAnalysisViewModel>();
        Matches = new ObservableCollection<RecentMatchItem>();
        Series = Array.Empty<ISeries>();
        XAxes = [new Axis { Labels = [] }];
        YAxes = [new Axis { MinLimit = 0, MaxLimit = 1.2 }];
    }

    public void OnNavigatedTo(object? parameter)
    {
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
                Profile = result.Profile;
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
        catch (OperationCanceledException)
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
                Fill = new SolidColorPaint(SKColor.Parse("#2FCB7A"))
            }
        ];

        XAxes =
        [
            new Axis
            {
                Labels = labels,
                LabelsRotation = 20,
                TextSize = 10,
                LabelsPaint = new SolidColorPaint(SKColor.Parse("#9AA6B5")),
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
                LabelsPaint = new SolidColorPaint(SKColor.Parse("#9AA6B5")),
                SeparatorsPaint = new SolidColorPaint(SKColor.Parse("#2A3441"))
            }
        ];
    }
}
