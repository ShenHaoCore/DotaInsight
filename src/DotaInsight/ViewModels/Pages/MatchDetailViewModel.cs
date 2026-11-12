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
        AdvantageSeries = Array.Empty<ISeries>();
    }

    public ObservableCollection<MatchPlayerItem> Radiant { get; }

    public ObservableCollection<MatchPlayerItem> Dire { get; }


    [ObservableProperty]
    private int bottomTabIndex;

    /// <summary>经济/经验优势曲线（两条 LineSeries）。</summary>
    [ObservableProperty]
    private ISeries[] advantageSeries;

    /// <summary>曲线有数据可画。</summary>
    public bool AdvantageHasData => AdvantageSeries.Length > 0;

    [ObservableProperty]
    private long matchId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBasic))]
    [NotifyPropertyChangedFor(nameof(HasDetail))]
    [NotifyPropertyChangedFor(nameof(IsUnparsed))]
    [NotifyPropertyChangedFor(nameof(ShowNotice))]
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
            if (player is not null)
            {
                Radiant.Add(player);
            }
        }

        foreach (var player in detail?.Dire ?? [])
        {
            if (player is not null)
            {
                Dire.Add(player);
            }
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



    /// <summary>
    /// 优势曲线 + 输出承伤图：与阵容同源（同一次 /matches 响应），纯客户端换算。
    /// </summary>
    private void RefreshMatchExtras(MatchDetail? detail)
    {
        RefreshAdvantageSeries(detail);
        BuildDamageChart(detail);
        BuildItemGoldChart(detail);
        BuildLastHitsChart(detail);
        BuildDotaPlusData(detail);
    }

    /// <summary>输出承伤对比图（双方全员按英雄伤害降序）。</summary>
    [ObservableProperty]
    private ISeries[] damageSeries = Array.Empty<ISeries>();

    [ObservableProperty]
    private Axis[] damageXAxes = [new() { Labels = [] }];

    [ObservableProperty]
    private Axis[] damageYAxes = [new() { Labels = [] }];

    public bool HasDamageChart => DamageSeries.Length > 0;

    /// <summary>图表右上角摘要。</summary>
    [ObservableProperty]
    private string damageSummaryText = string.Empty;

    /// <summary>
    /// 输出承伤图：双方全员按英雄伤害降序的横向条形。
    /// 加速局/未解析对局也有逐人伤害数据，所有比赛都可用。
    /// </summary>
    private void BuildDamageChart(MatchDetail? detail)
    {
        var players = (detail?.Radiant ?? []).Concat(detail?.Dire ?? [])
            .Where(p => p is not null && p.HeroDamage > 0)
            .OrderBy(p => p.HeroDamage)
            .ToList();
        var radiantSet = new HashSet<MatchPlayerItem>(detail?.Radiant ?? []);

        if (players.Count == 0)
        {
            DamageSeries = Array.Empty<ISeries>();
            OnPropertyChanged(nameof(HasDamageChart));
            return;
        }

        DamageSeries =
        [
            new RowSeries<double>
            {
                Name = "英雄伤害",
                Values = players.Select(p => (double)p.HeroDamage).ToArray(),
                MaxBarWidth = 22,
                Fill = new LinearGradientPaint(
                    new SKColor[] { SKColor.Parse("#1565C0"), SKColor.Parse("#42A5F5") },
                    new SKPoint(0, 0), new SKPoint(1, 0)),
                DataLabelsPaint = new SolidColorPaint(SKColor.Parse("#8A97A6")),
                DataLabelsSize = 10,
                DataLabelsPosition = LiveChartsCore.Measure.DataLabelsPosition.End,
                DataLabelsFormatter = point => $"{point.Model / 1000.0:F1}k"
            }
        ];

        DamageYAxes =
        [
            new Axis
            {
                Labels = players.Select(p => (radiantSet.Contains(p) ? "【天】" : "【夜】") + p.HeroName).ToArray(),
                TextSize = 11,
                LabelsPaint = new SolidColorPaint(SKColor.Parse("#8A97A6")),
                SeparatorsPaint = new SolidColorPaint(SKColors.Transparent)
            }
        ];

        DamageXAxes =
        [
            new Axis
            {
                TextSize = 10,
                LabelsPaint = new SolidColorPaint(SKColor.Parse("#8A97A6")),
                SeparatorsPaint = new SolidColorPaint(SKColor.Parse("#384351"))
            }
        ];

        DamageSummaryText = $"双方英雄伤害输出 · 最高 {players[^1].HeroName} {players[^1].HeroDamage / 1000.0:F1}k";
        OnPropertyChanged(nameof(HasDamageChart));
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
                GeometrySize = 2,
                Fill = new LinearGradientPaint(
                    new SKColor[] { new SKColor(0xE3, 0xB3, 0x41, 0x33), new SKColor(0xE3, 0xB3, 0x41, 0x00) },
                    new SKPoint(0, 0), new SKPoint(0, 1))
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

    /// <summary>装备投入对比（6 装备 + 中立物品购买总价，双方全员按投入升序）。</summary>
    [ObservableProperty]
    private ISeries[] itemGoldSeries = Array.Empty<ISeries>();

    [ObservableProperty]
    private Axis[] itemGoldXAxes = [new() { Labels = [] }];

    [ObservableProperty]
    private Axis[] itemGoldYAxes = [new() { Labels = [] }];

    public bool HasItemGold => ItemGoldSeries.Length > 0;

    [ObservableProperty]
    private string itemGoldSummary = string.Empty;

    /// <summary>补刀对比（双方全员按补刀数升序）。</summary>
    [ObservableProperty]
    private ISeries[] lastHitsSeries = Array.Empty<ISeries>();

    [ObservableProperty]
    private Axis[] lastHitsXAxes = [new() { Labels = [] }];

    [ObservableProperty]
    private Axis[] lastHitsYAxes = [new() { Labels = [] }];

    public bool HasLastHits => LastHitsSeries.Length > 0;

    [ObservableProperty]
    private string lastHitsSummary = string.Empty;

    /// <summary>装备投入：6 装备 + 中立物品购买总价（金币）。未解析对局为 0，整图空态。</summary>
    private void BuildItemGoldChart(MatchDetail? detail)
    {
        var players = (detail?.Radiant ?? []).Concat(detail?.Dire ?? [])
            .Where(p => p is not null && p.ItemGold > 0)
            .OrderBy(p => p.ItemGold)
            .ToList();

        if (players.Count == 0)
        {
            ItemGoldSeries = Array.Empty<ISeries>();
            OnPropertyChanged(nameof(HasItemGold));
            return;
        }

        ItemGoldSeries =
        [
            new RowSeries<double>
            {
                Name = "装备投入",
                Values = players.Select(p => (double)p.ItemGold).ToArray(),
                MaxBarWidth = 22,
                Fill = new LinearGradientPaint(
                    new SKColor[] { SKColor.Parse("#D9A120"), SKColor.Parse("#FFD54F") },
                    new SKPoint(0, 0), new SKPoint(1, 0)),
                DataLabelsPaint = new SolidColorPaint(SKColor.Parse("#8A97A6")),
                DataLabelsSize = 10,
                DataLabelsPosition = LiveChartsCore.Measure.DataLabelsPosition.End,
                DataLabelsFormatter = point => $"{point.Model:N0} 金"
            }
        ];

        ItemGoldYAxes =
        [
            new Axis
            {
                Labels = players.Select(TeamLabel).ToArray(),
                TextSize = 11,
                LabelsPaint = new SolidColorPaint(SKColor.Parse("#8A97A6")),
                SeparatorsPaint = new SolidColorPaint(SKColors.Transparent)
            }
        ];

        ItemGoldXAxes =
        [
            new Axis
            {
                TextSize = 10,
                LabelsPaint = new SolidColorPaint(SKColor.Parse("#8A97A6")),
                SeparatorsPaint = new SolidColorPaint(SKColor.Parse("#384351"))
            }
        ];

        var top = players[^1];
        ItemGoldSummary = $"双方装备投入 · 最高 {top.HeroName} {top.ItemGold:N0} 金";
        OnPropertyChanged(nameof(HasItemGold));
    }

    /// <summary>补刀对比：按补刀数升序（最大值画在顶部）。</summary>
    private void BuildLastHitsChart(MatchDetail? detail)
    {
        var players = (detail?.Radiant ?? []).Concat(detail?.Dire ?? [])
            .Where(p => p is not null)
            .OrderBy(p => p.LastHits)
            .ToList();

        if (players.Count == 0)
        {
            LastHitsSeries = Array.Empty<ISeries>();
            OnPropertyChanged(nameof(HasLastHits));
            return;
        }

        LastHitsSeries =
        [
            new RowSeries<double>
            {
                Name = "补刀",
                Values = players.Select(p => (double)p.LastHits).ToArray(),
                MaxBarWidth = 22,
                Fill = new LinearGradientPaint(
                    new SKColor[] { SKColor.Parse("#1A7F37"), SKColor.Parse("#66BB6A") },
                    new SKPoint(0, 0), new SKPoint(1, 0)),
                DataLabelsPaint = new SolidColorPaint(SKColor.Parse("#8A97A6")),
                DataLabelsSize = 10,
                DataLabelsPosition = LiveChartsCore.Measure.DataLabelsPosition.End,
                DataLabelsFormatter = point => $"{point.Model:F0} 刀"
            }
        ];

        LastHitsYAxes =
        [
            new Axis
            {
                Labels = players.Select(TeamLabel).ToArray(),
                TextSize = 11,
                LabelsPaint = new SolidColorPaint(SKColor.Parse("#8A97A6")),
                SeparatorsPaint = new SolidColorPaint(SKColors.Transparent)
            }
        ];

        LastHitsXAxes =
        [
            new Axis
            {
                TextSize = 10,
                LabelsPaint = new SolidColorPaint(SKColor.Parse("#8A97A6")),
                SeparatorsPaint = new SolidColorPaint(SKColor.Parse("#384351"))
            }
        ];

        var top = players[^1];
        LastHitsSummary = $"双方补刀对比 · 最高 {top.HeroName} {top.LastHits} 刀";
        OnPropertyChanged(nameof(HasLastHits));
    }

    /// <summary>玩家行标签：【天】/【夜】前缀 + 英雄名。</summary>
    private static string TeamLabel(MatchPlayerItem p)
        => (p.IsRadiant ? "【天】" : "【夜】") + p.HeroName;

    // ===== Dota Plus 风格：购买时间线 / 成长曲线 / 对线期 / 击杀矩阵 =====

    /// <summary>购买时间线行：每名玩家一行，图标按购买时刻定位。</summary>
    public ObservableCollection<PurchaseTimelineRow> PurchaseRows { get; } = new();

    [ObservableProperty]
    private bool hasPurchases;

    /// <summary>成长曲线玩家选择（全部十人）。</summary>
    public ObservableCollection<MatchPlayerItem> GrowthPlayers { get; } = new();

    [ObservableProperty]
    private MatchPlayerItem? selectedGrowthPlayer;

    [ObservableProperty]
    private ISeries[] growthSeries = Array.Empty<ISeries>();

    [ObservableProperty]
    private Axis[] growthXAxes = [new() { Labels = [] }];

    [ObservableProperty]
    private Axis[] growthYAxes = [new() { Labels = [] }];

    /// <summary>正补曲线（独立轴，量级小 50 倍）。</summary>
    [ObservableProperty]
    private ISeries[] growthLhSeries = Array.Empty<ISeries>();

    [ObservableProperty]
    private Axis[] growthLhXAxes = [new() { Labels = [] }];

    [ObservableProperty]
    private Axis[] growthLhYAxes = [new() { Labels = [] }];

    public bool HasGrowth => GrowthSeries.Length > 0;

    /// <summary>对线期行（10 分钟基准）。</summary>
    public ObservableCollection<LaningRow> LaningRows { get; } = new();

    [ObservableProperty]
    private bool hasLaning;

    /// <summary>击杀矩阵行（击杀者 → 各受害者次数）。</summary>
    public ObservableCollection<KillMatrixRow> MatrixRows { get; } = new();

    /// <summary>列头：受害者英雄名 + 阵营（与矩阵列序一致）。</summary>
    public ObservableCollection<MatrixHeaderCell> MatrixVictimHeader { get; } = new();

    [ObservableProperty]
    private bool hasMatrix;

    /// <summary>对线期评价行：按分路交叉配对（天辉优势路 vs 夜魇劣势路等）。</summary>
    public ObservableCollection<LaneVerdictRow> LaneVerdicts { get; } = new();

    [ObservableProperty]
    private bool hasLaneVerdicts;

    /// <summary>小地图移除后从构建器迁出的战况数据装配。</summary>
    private void BuildDotaPlusData(MatchDetail? detail)
    {
        PurchaseRows.Clear();
        GrowthPlayers.Clear();
        LaningRows.Clear();
        MatrixRows.Clear();
        LaneVerdicts.Clear();

        var duration = Math.Max(1, detail?.DurationSeconds ?? 1);
        var players = (detail?.Radiant ?? []).Concat(detail?.Dire ?? [])
            .Where(p => p is not null)
            .ToList();

        // 购买时间线：每名玩家一行
        foreach (var p in players)
        {
            var row = new PurchaseTimelineRow { PlayerLabel = TeamLabel(p), IsRadiant = p.IsRadiant };
            foreach (var e in p.Purchases)
            {
                row.Entries.Add(new PurchaseIconDot
                {
                    Left = e.Time / (double)duration * 720 + 10,
                    IconUrl = e.IconUrl,
                    Tooltip = $"{FormatClock(e.Time)} · {e.ItemName}" + (e.Cost > 0 ? $" · {e.Cost} 金" : string.Empty)
                });
            }

            PurchaseRows.Add(row);
        }

        HasPurchases = PurchaseRows.Any(r => r.Entries.Count > 0);

        // 成长曲线玩家选项：默认选中 GPM 最高的核心
        foreach (var p in players)
        {
            GrowthPlayers.Add(p);
        }

        SelectedGrowthPlayer = players.OrderByDescending(p => p.GoldPerMin).FirstOrDefault();

        // 对线期行：10 分钟基准
        foreach (var p in players)
        {
            LaningRows.Add(new LaningRow
            {
                PlayerLabel = TeamLabel(p),
                LaneName = string.IsNullOrWhiteSpace(p.LaneName) ? "未知分路" : p.LaneName,
                Lh10 = p.Lh10,
                Denies10 = p.Denies10,
                Gold10 = p.Gold10,
                IsRadiant = p.IsRadiant
            });
        }

        HasLaning = LaningRows.Count > 0;
        OnPropertyChanged(nameof(HasLaning));

        // 击杀矩阵：detail.KillMatrix（服务层按 NPC 名→英雄映射构建）
        if (detail is { KillMatrix.Count: > 0 })
        {
            for (var i = 0; i < detail.KillMatrix.Count && i < players.Count; i++)
            {
                var killer = players[i];
                var row = new KillMatrixRow
                {
                    KillerLabel = TeamLabel(killer),
                    IsRadiant = killer.IsRadiant
                };

                foreach (var count in detail.KillMatrix[i])
                {
                    row.Cells.Add(new MatrixCell
                    {
                        Text = count > 0 ? count.ToString() : string.Empty,
                        Heat = MatrixHeat(count),
                        IsRadiant = killer.IsRadiant
                    });
                }

                MatrixRows.Add(row);
            }
        }

        // 列头：受害者英雄名 + 阵营（天辉绿/夜魇红一眼区分击杀对象）
        MatrixVictimHeader.Clear();
        foreach (var p in players)
        {
            MatrixVictimHeader.Add(new MatrixHeaderCell { Name = p.HeroName, IsRadiant = p.IsRadiant });
        }

        HasMatrix = MatrixRows.Count > 0;
        OnPropertyChanged(nameof(HasMatrix));

        // 对线期评价：lane_role 是玩家自身视角，双方"优势路"并不对位，
        // 需交叉配对——天辉优势路(安全) ↔ 夜魇劣势路，中路对中路。
        BuildLaneVerdicts(players);
    }

    /// <summary>击杀数 → 热度底透明度：0 次不显示，5 次及以上封顶。</summary>
    private static double MatrixHeat(int count) => count switch
    {
        <= 0 => 0,
        1 => 0.22,
        2 => 0.38,
        3 => 0.54,
        4 => 0.70,
        _ => 0.85
    };

    /// <summary>按分路交叉配对，汇总 10 分钟正/反补与金币差，给出领先方评价。</summary>
    private void BuildLaneVerdicts(List<MatchPlayerItem> players)
    {
        var radiant = players.Where(p => p.IsRadiant).ToList();
        var dire = players.Where(p => !p.IsRadiant).ToList();

        AddLaneVerdict("天辉优势路", radiant, "优势路", dire, "劣势路");
        AddLaneVerdict("中路", radiant, "中路", dire, "中路");
        AddLaneVerdict("夜魇优势路", dire, "优势路", radiant, "劣势路");

        HasLaneVerdicts = LaneVerdicts.Count > 0;
        OnPropertyChanged(nameof(HasLaneVerdicts));
    }

    /// <summary>把一侧 team 的 laneName 玩家与另一侧 otherLane 玩家配成一条对线评价。</summary>
    private void AddLaneVerdict(
        string label,
        List<MatchPlayerItem> side, string laneName,
        List<MatchPlayerItem> otherSide, string otherLane)
    {
        var mine = side.Where(p => p.LaneName == laneName).ToList();
        var theirs = otherSide.Where(p => p.LaneName == otherLane).ToList();
        if (mine.Count == 0 || theirs.Count == 0)
        {
            return;
        }

        static (int Lh, int Dn, int Gold) Sum(List<MatchPlayerItem> ps)
            => (ps.Sum(x => x.Lh10), ps.Sum(x => x.Denies10), ps.Sum(x => x.Gold10));

        var a = Sum(mine);
        var b = Sum(theirs);
        var delta = (a.Lh + a.Dn) - (b.Lh + b.Dn);
        var aIsRadiant = mine[0].IsRadiant;
        var leaderIsRadiant = delta > 0 ? aIsRadiant : !aIsRadiant;

        LaneVerdicts.Add(new LaneVerdictRow
        {
            LaneLabel = label,
            SideAStats = $"{a.Lh + a.Dn} 补刀 · {a.Gold:N0} 金",
            SideBStats = $"{b.Lh + b.Dn} 补刀 · {b.Gold:N0} 金",
            SideAIsRadiant = aIsRadiant,
            Verdict = delta == 0
                ? "平分秋色"
                : $"{(leaderIsRadiant ? "天辉" : "夜魇")}领先 {Math.Abs(delta)} 补刀",
            RadiantLeads = delta == 0 || leaderIsRadiant,
            IsTie = delta == 0
        });
    }

    private static string FormatClock(int seconds)
        => seconds < 0
            ? "开局前"
            : $"{seconds / 60:00}:{seconds % 60:00}";

    partial void OnSelectedGrowthPlayerChanged(MatchPlayerItem? value)
        => RebuildGrowthSeries();

    /// <summary>成长曲线：金币/经验一图，正补独立一图（量级差 50 倍，合并会互相压扁）。</summary>
    private void RebuildGrowthSeries()
    {
        var p = SelectedGrowthPlayer;
        if (p is null || p.GoldT.Count == 0)
        {
            GrowthSeries = Array.Empty<ISeries>();
            GrowthLhSeries = Array.Empty<ISeries>();
            OnPropertyChanged(nameof(HasGrowth));
            return;
        }

        var minutes = p.GoldT.Count;
        var minuteLabels = Enumerable.Range(0, minutes).Select(m => m % 5 == 0 ? $"{m}'" : string.Empty).ToArray();

        GrowthSeries =
        [
            new LineSeries<double>
            {
                Name = "金币",
                Values = p.GoldT.Select(v => (double)v).ToArray(),
                Stroke = new SolidColorPaint(SKColor.Parse("#E3B341")) { StrokeThickness = 2 },
                GeometrySize = 3,
                GeometryFill = new SolidColorPaint(SKColor.Parse("#E3B341")),
                GeometryStroke = new SolidColorPaint(SKColor.Parse("#E3B341"))
            },
            new LineSeries<double>
            {
                Name = "经验",
                Values = p.XpT.Select(v => (double)v).ToArray(),
                Stroke = new SolidColorPaint(SKColor.Parse("#4FC3F7")) { StrokeThickness = 2 },
                GeometrySize = 3,
                GeometryFill = new SolidColorPaint(SKColor.Parse("#4FC3F7")),
                GeometryStroke = new SolidColorPaint(SKColor.Parse("#4FC3F7"))
            }
        ];

        GrowthXAxes =
        [
            new Axis
            {
                Labels = minuteLabels,
                TextSize = 10,
                LabelsPaint = new SolidColorPaint(SKColor.Parse("#8A97A6")),
                SeparatorsPaint = new SolidColorPaint(SKColors.Transparent)
            }
        ];

        GrowthYAxes =
        [
            new Axis
            {
                MinLimit = 0,
                MaxLimit = Math.Max(p.GoldT.Max(), p.XpT.Max()) * 1.15,
                TextSize = 10,
                LabelsPaint = new SolidColorPaint(SKColor.Parse("#8A97A6")),
                SeparatorsPaint = new SolidColorPaint(SKColor.Parse("#384351"))
            }
        ];

        GrowthLhSeries =
        [
            new LineSeries<double>
            {
                Name = "正补",
                Values = p.LhT.Select(v => (double)v).ToArray(),
                Stroke = new SolidColorPaint(SKColor.Parse("#2DA44E")) { StrokeThickness = 2 },
                GeometrySize = 4,
                GeometryFill = new SolidColorPaint(SKColor.Parse("#2DA44E")),
                GeometryStroke = new SolidColorPaint(SKColor.Parse("#2DA44E"))
            }
        ];

        GrowthLhXAxes =
        [
            new Axis
            {
                Labels = minuteLabels,
                TextSize = 10,
                LabelsPaint = new SolidColorPaint(SKColor.Parse("#8A97A6")),
                SeparatorsPaint = new SolidColorPaint(SKColor.Parse("#384351"))
            }
        ];

        GrowthLhYAxes =
        [
            new Axis
            {
                MinLimit = 0,
                MaxLimit = Math.Max(1, p.LhT.Max()) * 1.15,
                TextSize = 10,
                LabelsPaint = new SolidColorPaint(SKColor.Parse("#8A97A6")),
                SeparatorsPaint = new SolidColorPaint(SKColor.Parse("#384351"))
            }
        ];
    }

    /// <summary>成长曲线：金币/经验（左轴）+ 正补（右轴，逐分钟累计）。</summary>
    private static Task OnUiThreadAsync(Action action) => UiDispatcher.OnUiAsync(action);
}

/// <summary>购买时间线行：玩家标签 + 装备图标点集。</summary>
public sealed class PurchaseTimelineRow
{
    public string PlayerLabel { get; init; } = string.Empty;

    public bool IsRadiant { get; init; }

    public ObservableCollection<PurchaseIconDot> Entries { get; } = new();
}

/// <summary>购买图标点：时间轴上的像素定位。</summary>
public sealed class PurchaseIconDot
{
    public double Left { get; init; }

    public string IconUrl { get; init; } = string.Empty;

    public string Tooltip { get; init; } = string.Empty;
}

/// <summary>对线期行：10 分钟基准数据。</summary>
public sealed class LaningRow
{
    public string PlayerLabel { get; init; } = string.Empty;

    public string LaneName { get; init; } = string.Empty;

    public int Lh10 { get; init; }

    public int Denies10 { get; init; }

    public int Gold10 { get; init; }

    public bool IsRadiant { get; init; }
}

/// <summary>击杀矩阵列头：受害者英雄名 + 阵营。</summary>
public sealed class MatrixHeaderCell
{
    public string Name { get; init; } = string.Empty;

    public bool IsRadiant { get; init; }
}

/// <summary>击杀矩阵单元格：击杀次数文本 + 热度底（按击杀者阵营着色）。</summary>
public sealed class MatrixCell
{
    public string Text { get; init; } = string.Empty;

    /// <summary>热度底透明度：0 = 无击杀不显示，击杀越多越深。</summary>
    public double Heat { get; init; }

    /// <summary>击杀者阵营（决定底色：天辉绿/夜魇红）。</summary>
    public bool IsRadiant { get; init; }
}

/// <summary>击杀矩阵行：击杀者 + 对各受害者的击杀次数。</summary>
public sealed class KillMatrixRow
{
    public string KillerLabel { get; init; } = string.Empty;

    public bool IsRadiant { get; init; }

    public List<MatrixCell> Cells { get; } = new();
}

/// <summary>对线期评价行：一条分路的双方 10 分钟汇总与领先方判定。</summary>
public sealed class LaneVerdictRow
{
    public string LaneLabel { get; init; } = string.Empty;

    /// <summary>A 侧合计（正/反补 + 金币）。</summary>
    public string SideAStats { get; init; } = string.Empty;

    /// <summary>B 侧合计（正/反补 + 金币）。</summary>
    public string SideBStats { get; init; } = string.Empty;

    public bool SideAIsRadiant { get; init; }

    public string Verdict { get; init; } = string.Empty;

    /// <summary>领先方是否天辉（平局时用于中性着色，无副作用）。</summary>
    public bool RadiantLeads { get; init; }

    public bool IsTie { get; init; }
}
