using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotaInsight.Helpers;
using DotaInsight.Models;
using DotaInsight.Services;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using Serilog;
using SkiaSharp;

namespace DotaInsight.ViewModels.Pages;

/// <summary>
/// 比赛详情页 ViewModel：双方阵容、逐人数据与小地图战况层（眼位/建筑/热区/事件轴）。
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
        Wards = new ObservableCollection<MatchWardDot>();
        Buildings = new ObservableCollection<MatchBuildingDot>();
        EventTicks = new ObservableCollection<MapEventTick>();
        KillTicks = new ObservableCollection<MapEventTick>();
        AdvantageSeries = Array.Empty<ISeries>();
        _mapRefreshTimer.Tick += (_, _) =>
        {
            _mapRefreshTimer.Stop();
            ApplyMapState();
        };
    }

    public ObservableCollection<MatchPlayerItem> Radiant { get; }

    public ObservableCollection<MatchPlayerItem> Dire { get; }

    /// <summary>小地图眼位点（像素坐标已按 480 画布换算）。</summary>
    public ObservableCollection<MatchWardDot> Wards { get; }

    /// <summary>小地图建筑块（按当前时刻计算立起/倒塌）。</summary>
    public ObservableCollection<MatchBuildingDot> Buildings { get; }

    /// <summary>时间轴事件刻度（推塔/兵营/肉山/一血），点按跳转。</summary>
    public ObservableCollection<MapEventTick> EventTicks { get; }

    /// <summary>时间轴击杀刻度（细带，只有节奏没有坐标）。</summary>
    public ObservableCollection<MapEventTick> KillTicks { get; }

    /// <summary>底部区页签：0 阵容 / 1 经济曲线 / 2 小地图。</summary>
    [ObservableProperty]
    private int bottomTabIndex;

    /// <summary>回放时刻（秒）：-90 = 全部时间（与 OpenDota Vision 页同约定；建筑按终局状态展示）。</summary>
    [ObservableProperty]
    private double currentTime = -90;

    [ObservableProperty]
    private string currentTimeText = "全部时间";

    /// <summary>当前时刻可见的眼位数。</summary>
    public int VisibleWardCount => Wards.Count;

    /// <summary>经济/经验优势曲线（两条 LineSeries）。</summary>
    [ObservableProperty]
    private ISeries[] advantageSeries;

    /// <summary>曲线有数据可画。</summary>
    public bool AdvantageHasData => AdvantageSeries.Length > 0;

    /// <summary>该比赛有小地图分层数据（眼位或建筑事件）。</summary>
    public bool HasMapData => Detail is { HasMapLayers: true };

    /// <summary>该比赛有活动热区数据。</summary>
    [ObservableProperty]
    private bool hasHeatmap;

    /// <summary>热区位图（lane_pos 聚合渲染）。</summary>
    [ObservableProperty]
    private ImageSource? heatmapImage;

    // ===== 图层开关 =====
    [ObservableProperty]
    private bool showWards = true;

    [ObservableProperty]
    private bool showBuildings = true;

    /// <summary>热区默认关：信息密度大，作为分析工具按需打开。</summary>
    [ObservableProperty]
    private bool showHeatmap;

    /// <summary>当前时刻战况摘要（推塔/兵营/肉山/一血）。</summary>
    [ObservableProperty]
    private string mapSummaryText = string.Empty;

    [ObservableProperty]
    private long matchId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBasic))]
    [NotifyPropertyChangedFor(nameof(HasDetail))]
    [NotifyPropertyChangedFor(nameof(IsUnparsed))]
    [NotifyPropertyChangedFor(nameof(ShowNotice))]
    [NotifyPropertyChangedFor(nameof(HasMapData))]
    [NotifyPropertyChangedFor(nameof(IsAbilityDraft))]
    [NotifyPropertyChangedFor(nameof(HasAbilityDraft))]
    [NotifyPropertyChangedFor(nameof(HasDraftSteps))]
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

    /// <summary>技能征召（OMG）：有玩家带选到的技能数据。</summary>
    public bool IsAbilityDraft => Detail is { IsAbilityDraft: true };

    public IReadOnlyList<MatchPlayerItem> AbilityDraftRadiant
        => AbilityDraftPlayers(Detail?.Radiant);

    public IReadOnlyList<MatchPlayerItem> AbilityDraftDire
        => AbilityDraftPlayers(Detail?.Dire);

    public bool HasAbilityDraft => AbilityDraftRadiant.Count > 0 || AbilityDraftDire.Count > 0;

    /// <summary>Ban/Pick 序列（队长模式 / 队长征召）。</summary>
    public bool HasDraftSteps => Detail is { DraftSteps.Count: > 0 };

    private static IReadOnlyList<MatchPlayerItem> AbilityDraftPlayers(IReadOnlyList<MatchPlayerItem>? players)
        => players?.Where(p => p.DraftedAbilities.Count > 0).ToList() ?? [];

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

    /// <summary>点击时间轴刻度：跳到该事件时刻。</summary>
    [RelayCommand]
    private void JumpToTime(MapEventTick? tick)
    {
        if (tick is null)
        {
            return;
        }

        CurrentTime = Math.Max(0, tick.Time);
    }

    /// <summary>
    /// 时间轴拖动节流：拖动时 Value 每像素一变，眼位+建筑+摘要全量重建会卡顿——
    /// 60ms 合并一次重建（文字类轻更新仍即时）。
    /// </summary>
    private readonly System.Windows.Threading.DispatcherTimer _mapRefreshTimer = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromMilliseconds(60)
    };

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
            ResetMapState();

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
                Detail = detail;
                FillTeams(detail);
                RefreshMatchExtras(detail);
                StatusMessage = detail is null
                    ? "未找到该比赛：ID 可能无效，或 OpenDota 暂无这场数据"
                    : detail.HasPlayers
                        ? string.Empty
                        : "该比赛尚未被 Valve 解析，暂时只能看到基础战况";
            }).ConfigureAwait(true);

            await WarmItemIconsAsync(detail, token).ConfigureAwait(true);
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

    /// <summary>重建双方阵容集合（填充与装备图标刷新共用）。</summary>
    private void FillTeams(MatchDetail? detail)
    {
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
    }

    /// <summary>
    /// 装备图标可靠渲染：先把缺失图标下载落盘并清掉内存里可能已中毒的懒加载位图，
    /// 再重建双方集合让装备格绑定重新求值 —— 此时 Get 命中磁盘，同步解码必然出图。
    /// 冷路径原先依赖 WPF 懒下载（DelayCreation），失败后实例驻留 MemoryCache 永不自愈，
    /// 表现为「文件已落盘、装备格仍空白」。先渲染后补图，不阻塞页面主体。
    /// </summary>
    private async Task WarmItemIconsAsync(MatchDetail? detail, CancellationToken token)
    {
        try
        {
            var players = (detail?.Radiant ?? []).Concat(detail?.Dire ?? []).ToList();
            var itemUrls = players
                .SelectMany(p => p.ItemIconUrls.Concat([p.NeutralItemIconUrl]))
                .Where(u => !string.IsNullOrWhiteSpace(u))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (itemUrls.Count == 0)
            {
                return;
            }

            await Helpers.HeroImageCache.WarmUpAsync(itemUrls, decodeWidth: 64).ConfigureAwait(true);
            if (token.IsCancellationRequested)
            {
                return;
            }

            await OnUiThreadAsync(() =>
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                FillTeams(detail);
            }).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is OperationCanceledException && token.IsCancellationRequested)
        {
            // ignore
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "预热装备图标失败");
        }
    }

    /// <summary>比赛详情页小地图画布边长（px）。眼位/建筑/刻度的像素都按此换算。</summary>
    private const double MapCanvasSize = 480;

    private const double MapGridSize = 127.0;

    /// <summary>
    /// 优势曲线 + 小地图分层：与阵容同源（同一次 /matches 响应），纯客户端换算。
    /// </summary>
    private void RefreshMatchExtras(MatchDetail? detail)
    {
        CurrentTime = -90; // 换比赛重置为「全部时间」
        RefreshAdvantageSeries(detail);
        RefreshMapLayers(detail);
    }

    private void RefreshAdvantageSeries(MatchDetail? detail)
    {
        var gold = detail?.GoldAdvantage;
        var xp = detail?.XpAdvantage;

        if (gold is not { Count: > 0 })
        {
            AdvantageSeries = Array.Empty<ISeries>();
            OnPropertyChanged(nameof(AdvantageHasData));
            return;
        }

        AdvantageSeries =
        [
            new LineSeries<double?>
            {
                Name = "经济优势",
                Values = gold.Select(v => v is int i ? (double?)i : null).ToArray(),
                Stroke = new SolidColorPaint(SKColor.Parse("#E3B341")) { StrokeThickness = 2 },
                GeometryFill = new SolidColorPaint(SKColor.Parse("#E3B341")),
                GeometryStroke = new SolidColorPaint(SKColor.Parse("#E3B341")),
                GeometrySize = 2
            },
            new LineSeries<double?>
            {
                Name = "经验优势",
                Values = xp is { Count: > 0 }
                    ? xp.Select(v => v is int i ? (double?)i : null).ToArray()
                    : Array.Empty<double?>(),
                Stroke = new SolidColorPaint(SKColor.Parse("#4FC3F7")) { StrokeThickness = 2 },
                GeometryFill = new SolidColorPaint(SKColor.Parse("#4FC3F7")),
                GeometryStroke = new SolidColorPaint(SKColor.Parse("#4FC3F7")),
                GeometrySize = 2
            }
        ];
        OnPropertyChanged(nameof(AdvantageHasData));
    }

    // ===== 小地图分层 =====

    /// <summary>全部眼位（时间轴过滤的数据源）。</summary>
    private IReadOnlyList<MatchWardPlacement> _allWards = [];

    private IReadOnlyList<MatchBuilding> _allBuildings = [];

    private IReadOnlyList<MatchMapEvent> _allEvents = [];

    private int _durationSeconds = 1;

    private void ResetMapState()
    {
        _allWards = [];
        _allBuildings = [];
        _allEvents = [];
        Wards.Clear();
        Buildings.Clear();
        EventTicks.Clear();
        KillTicks.Clear();
        HeatmapImage = null;
        HasHeatmap = false;
        MapSummaryText = string.Empty;
        OnPropertyChanged(nameof(HasMapData));
        OnPropertyChanged(nameof(VisibleWardCount));
    }

    private void RefreshMapLayers(MatchDetail? detail)
    {
        _allWards = detail?.Wards ?? [];
        _allBuildings = detail?.Buildings ?? [];
        _allEvents = detail?.MapEvents ?? [];
        _durationSeconds = Math.Max(1, detail?.DurationSeconds ?? 1);

        HeatmapImage = HeatmapRenderer.Render(detail?.RadiantHeatmap, detail?.DireHeatmap);
        HasHeatmap = HeatmapImage is not null;

        BuildEventTicks();
        ApplyMapState();
        OnPropertyChanged(nameof(HasMapData));
    }

    /// <summary>时间轴上有建筑/肉山/一血刻度可显示。</summary>
    public bool HasEventTicks => EventTicks.Count > 0;

    /// <summary>时间轴上有击杀刻度可显示。</summary>
    public bool HasKillTicks => KillTicks.Count > 0;

    /// <summary>时间轴刻度：事件带（塔/兵营/肉山/一血）+ 击杀细带。</summary>
    private void BuildEventTicks()
    {
        EventTicks.Clear();
        KillTicks.Clear();

        foreach (var e in _allEvents)
        {
            var tick = new MapEventTick
            {
                Time = e.Time,
                Kind = e.Kind,
                IsRadiant = e.IsRadiant,
                Label = $"{FormatClock(e.Time)} · {e.Label}",
                // 事件带 480 宽，按时间比例定位
                Left = Math.Min(MapCanvasSize - 5, Math.Max(0, e.Time / (double)_durationSeconds * MapCanvasSize - 2.5))
            };

            if (e.Kind == MatchMapEventKind.Kill)
            {
                KillTicks.Add(tick);
            }
            else
            {
                EventTicks.Add(tick);
            }
        }

        OnPropertyChanged(nameof(HasEventTicks));
        OnPropertyChanged(nameof(HasKillTicks));
    }

    /// <summary>按当前时刻重建建筑状态与眼位，并刷新摘要。</summary>
    private void ApplyMapState()
    {
        ApplyBuildings();
        ApplyWardFilter();
        UpdateMapSummary();
    }

    private void ApplyBuildings()
    {
        Buildings.Clear();
        var t = (int)CurrentTime;

        foreach (var building in _allBuildings)
        {
            var standing = building.IsStandingAt(t);
            var size = building.IsAncient ? 18 : building.IsTower ? 12 : 10;
            Buildings.Add(new MatchBuildingDot
            {
                Left = building.LeftPct / 100.0 * MapCanvasSize - size / 2,
                Top = building.TopPct / 100.0 * MapCanvasSize - size / 2,
                Size = size,
                IsRadiant = building.IsRadiant,
                IsStanding = standing,
                IsAncient = building.IsAncient,
                IsBarracks = building.IsBarracks,
                Tooltip = standing
                    ? $"{building.DisplayName} · 仍立着"
                    : $"{building.DisplayName} · {FormatClock(building.FallsAt ?? 0)} 被 {building.KilledBy ?? "小兵"} 摧毁"
            });
        }
    }

    /// <summary>按回放时刻过滤眼位并重建小地图点。</summary>
    private void ApplyWardFilter()
    {
        Wards.Clear();
        var scale = MapCanvasSize / MapGridSize;
        var t = (int)CurrentTime;

        foreach (var ward in _allWards)
        {
            if (!ward.IsVisibleAt(t))
            {
                continue;
            }

            Wards.Add(new MatchWardDot
            {
                Left = ward.UvX * scale - 5.5,
                Top = ward.UvY * scale - 5.5,
                IsObserver = ward.IsObserver,
                IsRadiant = ward.IsRadiant,
                Tooltip = $"{FormatClock(Math.Max(0, ward.Time))} · {(ward.IsRadiant ? "天辉" : "夜魇")} · {(ward.IsObserver ? "观察守卫" : "岗哨守卫")}"
            });
        }

        OnPropertyChanged(nameof(VisibleWardCount));
    }

    /// <summary>当前时刻战况摘要：推塔/兵营/肉山计数 + 一血。</summary>
    private void UpdateMapSummary()
    {
        if (_allEvents.Count == 0)
        {
            MapSummaryText = string.Empty;
            return;
        }

        var t = (int)CurrentTime;
        var scope = _allEvents.Where(e => t < 0 || e.Time <= t).ToList();

        int towerR = 0, towerD = 0, raxR = 0, raxD = 0, roshR = 0, roshD = 0, tfR = 0, tfD = 0;
        var firstBlood = false;
        foreach (var e in scope)
        {
            switch (e.Kind)
            {
                case MatchMapEventKind.Tower:
                    if (e.IsRadiant) towerR++;
                    else towerD++;
                    break;
                case MatchMapEventKind.Barracks:
                    if (e.IsRadiant) raxR++;
                    else raxD++;
                    break;
                case MatchMapEventKind.Roshan:
                    if (e.IsRadiant) roshR++;
                    else roshD++;
                    break;
                case MatchMapEventKind.TeamFight:
                    if (e.IsRadiant) tfR++;
                    else tfD++;
                    break;
                case MatchMapEventKind.FirstBlood:
                    firstBlood = true;
                    break;
            }
        }

        var parts = new List<string> { $"推塔 天{towerR} 夜{towerD}" };
        if (raxR + raxD > 0)
        {
            parts.Add($"兵营 天{raxR} 夜{raxD}");
        }

        if (roshR + roshD > 0)
        {
            parts.Add($"肉山 天{roshR} 夜{roshD}");
        }

        if (tfR + tfD > 0)
        {
            parts.Add($"团战 天{tfR} 夜{tfD}");
        }

        if (firstBlood)
        {
            parts.Add("一血已出");
        }

        MapSummaryText = (t < 0 ? "终局 · " : $"{FormatClock(t)} · ") + string.Join(" · ", parts);
    }

    private static string FormatClock(int seconds)
        => seconds < 0
            ? "开局前"
            : $"{seconds / 60:00}:{seconds % 60:00}";

    partial void OnCurrentTimeChanged(double value)
    {
        CurrentTimeText = value <= -90
            ? "全部时间"
            : value < 0
                ? "开局阶段"
                : $"{(int)value / 60:00}:{(int)value % 60:00}";

        if (_allWards.Count > 0 || _allBuildings.Count > 0 || _allEvents.Count > 0)
        {
            // 重建合并到 60ms 节流器：拖动结束后的最后一次状态必然被应用
            _mapRefreshTimer.Stop();
            _mapRefreshTimer.Start();
        }
    }

    partial void OnShowWardsChanged(bool value) => ApplyWardFilter();

    partial void OnShowBuildingsChanged(bool value) => ApplyBuildings();

    private static Task OnUiThreadAsync(Action action) => UiDispatcher.OnUiAsync(action);
}

/// <summary>小地图眼位点：480 画布上的像素定位 + 展示元数据。</summary>
public sealed class MatchWardDot
{
    public double Left { get; init; }

    public double Top { get; init; }

    public bool IsObserver { get; init; }

    public bool IsRadiant { get; init; }

    public string Tooltip { get; init; } = string.Empty;
}

/// <summary>小地图建筑块：按当前时刻的立起/倒塌状态渲染。</summary>
public sealed class MatchBuildingDot
{
    public double Left { get; init; }

    public double Top { get; init; }

    /// <summary>边长：塔 12 / 兵营 10 / 遗迹 18。</summary>
    public double Size { get; init; }

    public bool IsRadiant { get; init; }

    public bool IsStanding { get; init; }

    public bool IsAncient { get; init; }

    public bool IsBarracks { get; init; }

    /// <summary>圆角：塔=2 / 兵营=5 / 遗迹=9（圆）。</summary>
    public double CornerRadius => IsAncient ? 9 : IsBarracks ? 5 : 2;

    public string Tooltip { get; init; } = string.Empty;
}

/// <summary>时间轴刻度：时间 + 种类 + 悬停文案 + 事件带上的像素位置。</summary>
public sealed class MapEventTick
{
    public int Time { get; init; }

    public MatchMapEventKind Kind { get; init; }

    public bool IsRadiant { get; init; }

    public string Label { get; init; } = string.Empty;

    public double Left { get; init; }
}
