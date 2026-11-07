using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
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
    private readonly ISteamAvatarFrameService _frameService;
    private readonly ILiteDbCacheService _cache;
    private readonly INavigationService _navigation;
    private readonly MainWindowViewModel _shell;
    private readonly ILogger _logger;
    private CancellationTokenSource? _cts;

    /// <summary>头像框查询的轮次：新查询发起后，旧查询迟到的框结果作废。</summary>
    private int _frameQueryEpoch;

    public MatchAnalysisViewModel(
        IMatchAnalysisService matchService,
        IAccountService accountService,
        ISteamAvatarFrameService frameService,
        ILiteDbCacheService cache,
        INavigationService navigation,
        MainWindowViewModel shell,
        ILogger logger)
    {
        _matchService = matchService;
        _accountService = accountService;
        _frameService = frameService;
        _cache = cache;
        _navigation = navigation;
        _shell = shell;
        _logger = logger.ForContext<MatchAnalysisViewModel>();
        Matches = new ObservableCollection<RecentMatchItem>();
        Accounts = new ObservableCollection<SavedAccount>();
        RecentHeroes = new ObservableCollection<RecentHeroSummary>();
        Findings = new ObservableCollection<PlaystyleFinding>();
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

    /// <summary>打法分析结论（待改进优先），与比赛列表同源聚合。</summary>
    public ObservableCollection<PlaystyleFinding> Findings { get; }

    /// <summary>已保存的账号（最近使用在前），做成卡片用于一键切换。</summary>
    public ObservableCollection<SavedAccount> Accounts { get; }

    /// <summary>近期对局按英雄聚合的常用英雄卡（对局数降序）。</summary>
    public ObservableCollection<RecentHeroSummary> RecentHeroes { get; }

    public bool HasAccounts => Accounts.Count > 0;

    public bool HasRecentHeroes => RecentHeroes.Count > 0;

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

    /// <summary>当前查询玩家的 Steam 头像框图地址；无框 / 被墙 / 未加载为空。</summary>
    [ObservableProperty]
    private string avatarFrameUrl = string.Empty;

    partial void OnAvatarFrameUrlChanged(string value) => OnPropertyChanged(nameof(HasAvatarFrame));

    public bool HasAvatarFrame => !string.IsNullOrWhiteSpace(AvatarFrameUrl);

    [ObservableProperty]
    private string recentSummary = string.Empty;

    [ObservableProperty]
    private string playstyleSummary = string.Empty;

    /// <summary>底部区页签：0 胜负走势 / 1 打法建议。</summary>
    [ObservableProperty]
    private int bottomTabIndex;

    public bool HasFindings => Findings.Count > 0;

    [ObservableProperty]
    private ISeries[] series;

    [ObservableProperty]
    private Axis[] xAxes;

    [ObservableProperty]
    private Axis[] yAxes;

    [RelayCommand]
    private async Task SearchAsync() => await RunSearchAsync(forceRefresh: false);

    /// <summary>强制刷新：忽略缓存重新拉取该账号的全部数据。</summary>
    [RelayCommand]
    private async Task RefreshDataAsync() => await RunSearchAsync(forceRefresh: true);

    private async Task RunSearchAsync(bool forceRefresh)
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
        // 换账号查询时先清掉上一个人的框，避免串框
        AvatarFrameUrl = string.Empty;
        _frameQueryEpoch++;
        var frameEpoch = _frameQueryEpoch;

        try
        {
            IsLoading = true;
            HasData = false;
            StatusMessage = $"正在查询账号 {accountId}...";
            _logger.Information("开始战绩分析 AccountId={AccountId}", accountId);

            var result = await _matchService.AnalyzeAsync(accountId, forceRefresh, token).ConfigureAwait(true);
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
                    // 头像框独立于主查询：数据在 steamcommunity（可能被墙），
                    // 失败静默为无框，不拖慢也不影响战绩展示
                    _ = LoadAvatarFrameAsync(accountId, frameEpoch, token);
                }

                _allMatches = result.Matches;
                RebuildHeroFilterOptions();
                ApplyMatchFilters();

                RefreshRecentHeroes(result.Matches);
                RefreshPlaystyle(result);
                UpdateChart(result.Matches);
                HasData = !result.IsEmpty;
                WarmUpMatchImages(result.Matches);
                _ = LoadHeroWinratesAsync(accountId, forceRefresh, frameEpoch, token);
                _ = LoadPlayerActivityAsync(accountId, forceRefresh, frameEpoch, token);

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
                            : forceRefresh
                                ? "已强制刷新"
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

    /// <summary>
    /// 异步补拉头像框：命中缓存则几乎即时，未命中走资料页抓取（服务内部静默降级）。
    /// 迟到结果按轮次作废，避免换账号后串框。
    /// </summary>
    private async Task LoadAvatarFrameAsync(long accountId, int frameEpoch, CancellationToken token)
    {
        try
        {
            var url = await _frameService.GetFrameUrlAsync(accountId, token).ConfigureAwait(true);
            if (frameEpoch != _frameQueryEpoch || token.IsCancellationRequested)
            {
                return;
            }

            AvatarFrameUrl = url ?? string.Empty;
        }
        catch (Exception ex)
        {
            // 含用户取消与网络失败：框是装饰，不值得任何打扰
            _logger.Debug(ex, "头像框加载跳过 AccountId={AccountId}", accountId);
        }
    }

    // ===== 比赛筛选 =====

    private IReadOnlyList<RecentMatchItem> _allMatches = [];

    /// <summary>胜负筛选：0 全部 / 1 胜 / 2 负。</summary>
    [ObservableProperty]
    private int resultFilter;

    /// <summary>模式筛选：0 全部 / 1 常规 / 2 加速。</summary>
    [ObservableProperty]
    private int modeFilter;

    /// <summary>英雄筛选（英雄名）。</summary>
    [ObservableProperty]
    private string? heroFilter = AllHeroesFilter;

    /// <summary>英雄筛选下拉的候选。</summary>
    public ObservableCollection<string> HeroFilterOptions { get; } = [AllHeroesFilter];

    /// <summary>列表头计数：筛选后与总数不同时显示「N / M 场」。</summary>
    [ObservableProperty]
    private string matchCountText = string.Empty;

    /// <summary>筛选后仍有对局可展示。</summary>
    public bool HasVisibleMatches => Matches.Count > 0;

    /// <summary>已查到数据但筛后为空：显示「没有符合筛选条件的对局」而不是「未查询」。</summary>
    public bool HasFilteredEmpty => HasData && Matches.Count == 0;

    /// <summary>英雄筛选的「不过滤」占位项。</summary>
    public const string AllHeroesFilter = "全部英雄";

    partial void OnResultFilterChanged(int value) => ApplyMatchFilters();

    partial void OnModeFilterChanged(int value) => ApplyMatchFilters();

    partial void OnHeroFilterChanged(string? value) => ApplyMatchFilters();

    private void RebuildHeroFilterOptions()
    {
        HeroFilterOptions.Clear();
        HeroFilterOptions.Add(AllHeroesFilter);
        foreach (var name in _allMatches.Select(m => m.HeroName).Distinct())
        {
            HeroFilterOptions.Add(name);
        }

        HeroFilter = AllHeroesFilter;
    }

    /// <summary>按胜负 / 模式 / 英雄过滤比赛列表（纯客户端，不重新请求）。</summary>
    private void ApplyMatchFilters()
    {
        IEnumerable<RecentMatchItem> query = _allMatches;

        if (ResultFilter == 1)
        {
            query = query.Where(m => m.IsWin);
        }
        else if (ResultFilter == 2)
        {
            query = query.Where(m => !m.IsWin);
        }

        if (ModeFilter == 1)
        {
            query = query.Where(m => m.GameMode != 23);
        }
        else if (ModeFilter == 2)
        {
            query = query.Where(m => m.GameMode == 23);
        }

        if (!string.IsNullOrWhiteSpace(HeroFilter) && HeroFilter != AllHeroesFilter)
        {
            query = query.Where(m => m.HeroName == HeroFilter);
        }

        Matches.Clear();
        foreach (var match in query)
        {
            Matches.Add(match);
        }

        MatchCountText = Matches.Count == _allMatches.Count
            ? $"{_allMatches.Count} 场"
            : $"{Matches.Count} / {_allMatches.Count} 场";
        OnPropertyChanged(nameof(HasVisibleMatches));
        OnPropertyChanged(nameof(HasFilteredEmpty));
    }

    /// <summary>
    /// 比赛图标与头像后台预热，落盘后重放筛选让绑定重新求值——
    /// HeroImageCache 冷未命中在 UI 线程返回空位，靠这一步补图。
    /// </summary>
    private void WarmUpMatchImages(IReadOnlyList<RecentMatchItem> matches)
    {
        var urls = matches
            .Select(m => m.IconUrl)
            .Concat([Profile?.AvatarUrl ?? string.Empty, AvatarFrameUrl])
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (urls.Count == 0)
        {
            return;
        }

        _ = Helpers.HeroImageCache.WarmUpAsync(urls, decodeWidth: 120).ContinueWith(
            _ => Helpers.UiDispatcher.OnUiAsync(ApplyMatchFilters),
            TaskScheduler.Default);
    }

    // ===== 英雄胜率榜 =====

    /// <summary>生涯英雄胜率（按场次降序，最多 10 条）。</summary>
    public ObservableCollection<PlayerHeroStat> HeroWinrates { get; } = new();

    public bool HasHeroWinrates => HeroWinrates.Count > 0;

    /// <summary>
    /// 异步补拉生涯英雄胜率：独立于主查询（失败静默），迟到结果按轮次作废。
    /// 装饰性数据延到首帧之后（250ms）再拉，避免与首屏渲染争抢 UI 线程。
    /// </summary>
    private async Task LoadHeroWinratesAsync(long accountId, bool forceRefresh, int epoch, CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), token).ConfigureAwait(true);
            var stats = await _matchService.GetPlayerHeroStatsAsync(accountId, forceRefresh, token).ConfigureAwait(true);
            if (epoch != _frameQueryEpoch || token.IsCancellationRequested)
            {
                return;
            }

            HeroWinrates.Clear();
            foreach (var stat in stats)
            {
                HeroWinrates.Add(stat);
            }

            OnPropertyChanged(nameof(HasHeroWinrates));
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "英雄胜率榜加载跳过 AccountId={AccountId}", accountId);
        }
    }

    /// <summary>点击胜率榜条目：跳到该英雄的详情页。</summary>
    [RelayCommand]
    private void OpenHeroStat(PlayerHeroStat? stat)
    {
        if (stat is null || stat.HeroId <= 0)
        {
            return;
        }

        _shell.CurrentPageTitle = "英雄详情";
        _navigation.NavigateTo(MainWindowViewModel.HeroDetailPageKey, stat.HeroId);
    }

    // ===== 每日游玩热力图 =====

    /// <summary>日历位图（53×7 预渲染，单元素零布局成本）。</summary>
    [ObservableProperty]
    private ImageSource? activityImage;

    /// <summary>位图宽度（供 Image 定宽）。</summary>
    [ObservableProperty]
    private double activityImageWidth;

    /// <summary>位图高度（格子自适应后随之变化）。</summary>
    [ObservableProperty]
    private double activityImageHeight = 84;

    /// <summary>月标签单元宽度（= 格子步长，与位图列对齐）。</summary>
    [ObservableProperty]
    private double activityMonthLabelWidth = 12;

    /// <summary>当前渲染使用的格子边长（悬浮命中反查与渲染保持一致）。</summary>
    public double ActivityCellSize { get; private set; } = Helpers.ActivityCalendarRenderer.CellSize;

    /// <summary>扁平格清单（列优先），供悬浮提示按坐标反查。</summary>
    public IReadOnlyList<PlayerActivityDay> ActivityCells { get; private set; } = [];

    /// <summary>最近一次构建的热力图数据，供格子自适应后重渲。</summary>
    private PlayerActivityHeatmap? _activityHeatmap;

    /// <summary>月标签行（与位图列一一对应）。</summary>
    public IReadOnlyList<string?> ActivityMonthLabels { get; private set; } = [];

    public bool HasActivity => ActivityImage is not null;

    /// <summary>标题右侧摘要（总场次 / 活跃天数 / 最多一天）。</summary>
    [ObservableProperty]
    private string activitySummaryText = string.Empty;

    /// <summary>
    /// 异步补拉每日游玩热力图：独立于主查询（失败静默），迟到结果按轮次作废。
    /// 装饰性数据延到首帧之后（Background 优先级）再拉，避免与首屏渲染争抢 UI 线程。
    /// </summary>
    private async Task LoadPlayerActivityAsync(long accountId, bool forceRefresh, int epoch, CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), token).ConfigureAwait(true);

            var heatmap = await _matchService.GetPlayerActivityAsync(accountId, forceRefresh, token).ConfigureAwait(true);
            if (epoch != _frameQueryEpoch || token.IsCancellationRequested)
            {
                return;
            }

            if (heatmap is null || heatmap.Weeks.Count == 0)
            {
                _activityHeatmap = null;
                ActivityImage = null;
                ActivityCells = [];
                ActivityMonthLabels = [];
                OnPropertyChanged(nameof(ActivityMonthLabels));
                OnPropertyChanged(nameof(HasActivity));
                return;
            }

            _activityHeatmap = heatmap;
            ActivityCells = heatmap.Weeks.SelectMany(w => w.Days).ToList();
            ActivityMonthLabels = heatmap.Weeks.Select(w => w.MonthLabel).ToList();
            ActivitySummaryText = heatmap.SummaryText;
            // 普通属性无自动通知：列表晚于绑定求值到达，必须手动通知 ItemsControl 刷新
            OnPropertyChanged(nameof(ActivityMonthLabels));
            RebuildActivityImage(null);
            OnPropertyChanged(nameof(HasActivity));
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "每日游玩热力图加载跳过 AccountId={AccountId}", accountId);
        }
    }

    /// <summary>最近一次容器可用宽度（SizeChanged 时记录），数据晚于布局到达时复用。</summary>
    private double _activityAvailableWidth;

    /// <summary>
    /// 按可用宽度自适应重渲热力图：格子尽量放大铺满容器，钳制在 [10, 18]，
    /// 低于下限保持 10（窄窗出滚动条）。availableWidth 为 null 时用最近记录的宽度。
    /// </summary>
    public void RebuildActivityImage(double? availableWidth)
    {
        if (_activityHeatmap is null || _activityHeatmap.Weeks.Count == 0)
        {
            return;
        }

        if (availableWidth is { } w && w > 0)
        {
            _activityAvailableWidth = w;
        }

        var gap = Helpers.ActivityCalendarRenderer.GapSize;
        var source = availableWidth ?? (_activityAvailableWidth > 0 ? _activityAvailableWidth : null);
        var cell = source is { } width
            ? Helpers.ActivityCalendarRenderer.ComputeCellSize(width, _activityHeatmap.Weeks.Count)
            : ActivityCellSize;
        if (Math.Abs(cell - ActivityCellSize) < 0.5 && ActivityImage is not null)
        {
            return; // 尺寸未变，跳过重渲
        }

        ActivityCellSize = cell;
        ActivityImage = Helpers.ActivityCalendarRenderer.Render(_activityHeatmap.Weeks, cell, gap);
        ActivityImageWidth = Helpers.ActivityCalendarRenderer.WidthFor(_activityHeatmap.Weeks.Count, cell, gap);
        ActivityImageHeight = 7 * (cell + gap);
        ActivityMonthLabelWidth = cell + gap;
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
            RecentHeroes.Clear();
            Findings.Clear();
            PlaystyleSummary = string.Empty;
            OnPropertyChanged(nameof(HasRecentHeroes));
            OnPropertyChanged(nameof(HasFindings));
            HasData = false;
            RecentSummary = string.Empty;
            StatusMessage = "已移除该账号，可输入 ID 重新查询";
        }

        RefreshAccounts();
    }

    /// <summary>重建「近期常用英雄」卡：数据与比赛列表同源，纯客户端聚合。</summary>
    private void RefreshRecentHeroes(IReadOnlyList<RecentMatchItem> matches)
    {
        RecentHeroes.Clear();
        foreach (var summary in RecentHeroSummary.Summarize(matches))
        {
            RecentHeroes.Add(summary);
        }

        OnPropertyChanged(nameof(HasRecentHeroes));
    }

    /// <summary>
    /// 与比赛列表同源的打法结论：不再发额外请求，聚合已有对位行后过规则引擎。
    /// </summary>
    private void RefreshPlaystyle(MatchAnalysisResult result)
    {
        Findings.Clear();
        var sample = PlaystyleAdvisor.BuildSample(result.Matches);
        var report = PlaystyleAdvisor.Evaluate(result.Profile?.AccountId ?? 0, sample);
        PlaystyleSummary = report.SummaryText;
        foreach (var finding in report.Findings)
        {
            Findings.Add(finding);
        }

        OnPropertyChanged(nameof(HasFindings));
    }

    /// <summary>点击常用英雄卡：进入该英雄详情页（与图鉴/首页行为一致）。</summary>
    [RelayCommand]
    private void OpenRecentHero(RecentHeroSummary? summary)
    {
        if (summary is null || summary.HeroId <= 0)
        {
            return;
        }

        _shell.CurrentPageTitle = "英雄详情";
        _shell.ActiveNav = MainWindowViewModel.HeroGalleryPageKey;
        _navigation.NavigateTo(MainWindowViewModel.HeroDetailPageKey, summary.HeroId);
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
