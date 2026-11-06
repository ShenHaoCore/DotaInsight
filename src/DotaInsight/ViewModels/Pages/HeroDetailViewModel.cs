using System.Collections.ObjectModel;
using System.Windows;
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
/// 英雄详情页：国服资料、属性面板、段位与克制预览。
/// </summary>
public partial class HeroDetailViewModel : ObservableObject, INavigationAware
{
    private readonly IHeroCounterService _heroService;
    private readonly IHeroProfileService _profileService;
    private readonly INavigationService _navigation;
    private readonly MainWindowViewModel _shell;
    private readonly ILogger _logger;
    private CancellationTokenSource? _cts;
    private int? _loadedHeroId;
    private int _loadGeneration;

    public HeroDetailViewModel(
        IHeroCounterService heroService,
        IHeroProfileService profileService,
        INavigationService navigation,
        MainWindowViewModel shell,
        ILogger logger)
    {
        _heroService = heroService;
        _profileService = profileService;
        _navigation = navigation;
        _shell = shell;
        _logger = logger.ForContext<HeroDetailViewModel>();
        CounteredByList = new ObservableCollection<HeroCounterItem>();
        CountersList = new ObservableCollection<HeroCounterItem>();
        MatchupSeries = Array.Empty<ISeries>();
        MatchupXAxes = [new Axis { Labels = [], LabelsPaint = new SolidColorPaint(SKColor.Parse("#8A97A6")) }];
        MatchupYAxes =
        [
            new Axis
            {
                TextSize = 10,
                LabelsPaint = new SolidColorPaint(SKColor.Parse("#8A97A6")),
                SeparatorsPaint = new SolidColorPaint(SKColor.Parse("#232A36"))
            }
        ];
        RoleStats = new ObservableCollection<HeroRoleStat>();
        NormalAbilities = new ObservableCollection<HeroAbilityInfo>();
        InnateAbilities = new ObservableCollection<HeroAbilityInfo>();
        TalentRows = new ObservableCollection<HeroTalentRow>();
    }

    /// <summary>完整克制列表（吸收独立克制页后的全量数据，最多 40 条）。</summary>
    public ObservableCollection<HeroCounterItem> CounteredByList { get; }

    public ObservableCollection<HeroCounterItem> CountersList { get; }

    /// <summary>对位优势分布图（克制页迁入）。</summary>
    [ObservableProperty]
    private ISeries[] matchupSeries;

    [ObservableProperty]
    private Axis[] matchupXAxes;

    [ObservableProperty]
    private Axis[] matchupYAxes;

    /// <summary>分布图有数据可画。</summary>
    public bool MatchupHasData => MatchupSeries.Length > 0;

    /// <summary>克制页签的英雄切换列表（英雄列表就绪时填充，零额外请求）。</summary>
    [ObservableProperty]
    private IReadOnlyList<HeroStat>? pickOptions;

    [ObservableProperty]
    private HeroStat? pickedHero;

    public ObservableCollection<HeroRoleStat> RoleStats { get; }

    /// <summary>常规技能（含神杖/魔晶升级技能），按官网顺序排列。</summary>
    public ObservableCollection<HeroAbilityInfo> NormalAbilities { get; }

    /// <summary>先天技能（自带被动/特性），单独分区展示。</summary>
    public ObservableCollection<HeroAbilityInfo> InnateAbilities { get; }

    public ObservableCollection<HeroTalentRow> TalentRows { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHero))]
    [NotifyPropertyChangedFor(nameof(HasBrackets))]
    [NotifyPropertyChangedFor(nameof(ShowHeroPlaceholder))]
    [NotifyPropertyChangedFor(nameof(ShowProfileMissing))]
    [NotifyPropertyChangedFor(nameof(TurnRateText))]
    private HeroStat? hero;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProfile))]
    [NotifyPropertyChangedFor(nameof(HasBio))]
    [NotifyPropertyChangedFor(nameof(HasInnateAbilities))]
    [NotifyPropertyChangedFor(nameof(HasTalents))]
    [NotifyPropertyChangedFor(nameof(ComplexityLevel))]
    [NotifyPropertyChangedFor(nameof(HasComplexity1))]
    [NotifyPropertyChangedFor(nameof(HasComplexity2))]
    [NotifyPropertyChangedFor(nameof(HasComplexity3))]
    [NotifyPropertyChangedFor(nameof(ShowProfileMissing))]
    [NotifyPropertyChangedFor(nameof(TurnRateText))]
    private HeroDetailProfile? profile;

    [ObservableProperty]
    private HeroAbilityInfo? selectedAbility;

    public bool HasHero => Hero is not null;

    public bool HasProfile => Profile is not null;

    public bool HasBio => !string.IsNullOrWhiteSpace(Profile?.Bio);

    public bool HasInnateAbilities => InnateAbilities.Count > 0;

    public bool HasTalents => TalentRows.Count > 0;

    public int ComplexityLevel => Profile?.Complexity ?? 0;

    public bool HasComplexity1 => ComplexityLevel >= 1;

    public bool HasComplexity2 => ComplexityLevel >= 2;

    public bool HasComplexity3 => ComplexityLevel >= 3;

    public bool HasBrackets => Hero?.Brackets is { Count: > 0 };

    public bool HasMatchupPreview => CounteredByList.Count > 0 || CountersList.Count > 0;

    /// <summary>已开始加载但英雄基础信息尚未就绪：显示占位，避免整块 banner 消失导致版式跳动。</summary>
    public bool ShowHeroPlaceholder => IsLoading && Hero is null;

    /// <summary>
    /// 资料确实缺失时才提示。必须在加载中排除，否则每次换英雄都会闪一下「暂无该英雄的详细资料」。
    /// </summary>
    public bool ShowProfileMissing => !IsLoading && Hero is not null && Profile is null;

    /// <summary>
    /// 转身速率：OpenDota heroStats 的 turn_rate 对多数英雄返回 null（实测 127 个中 82 个为 0），
    /// 由国服 herodata 的值补全。
    /// 放在 VM 上而不是 HeroStat 上：HeroStat 是无属性通知的普通对象，
    /// 若把补全值写回 HeroStat，就必须保证「先设值再赋 Hero」，
    /// 那样两段式加载（先渲染基础信息、后到详情资料）永远拿不到正确值。
    /// 判定占位时只看「详情资料到了没有」，不能看 IsLoading：
    /// IsLoading 一直持续到对位预览结束，而转身速率在详情资料到达时就已知，
    /// 用它当条件会让这个数字比旁边的技能/背景白晚好几秒。
    /// </summary>
    public string TurnRateText
    {
        get
        {
            var rate = Profile?.TurnRate > 0 ? Profile.TurnRate : Hero?.TurnRate ?? 0;
            if (rate > 0)
            {
                return rate.ToString("0.##");
            }

            // 还没取到值：资料在途就显示占位，别急着判成「缺数据」闪一个「—」。
            return Profile is null && IsLoading ? "…" : "—";
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowHeroPlaceholder))]
    [NotifyPropertyChangedFor(nameof(ShowProfileMissing))]
    [NotifyPropertyChangedFor(nameof(TurnRateText))]
    private bool isLoading;

    [ObservableProperty]
    private bool isLoadingMatchups;

    [ObservableProperty]
    private string statusMessage = string.Empty;

    [ObservableProperty]
    private string matchupStatus = string.Empty;

    public void OnNavigatedTo(object? parameter)
    {
        if (parameter is int heroId)
        {
            if (_loadedHeroId == heroId
                && Hero is not null
                && string.IsNullOrEmpty(StatusMessage)
                && !IsLoading)
            {
                return;
            }

            _ = LoadAsync(heroId);
            return;
        }

        if (parameter is HeroStat stat)
        {
            _ = LoadFromStatAsync(stat);
        }
    }

    /// <summary>离开详情页时取消进行中的加载。</summary>
    public void CancelPendingLoads()
    {
        _loadGeneration++;
        try
        {
            _cts?.Cancel();
        }
        catch
        {
            // ignore
        }
    }

    private async Task LoadFromStatAsync(HeroStat stat)
        => await LoadAsync(stat.Id).ConfigureAwait(true);

    [RelayCommand]
    private async Task LoadAsync(int heroId)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var generation = ++_loadGeneration;

        try
        {
            // 关键：先整体清空上一个英雄的内容再开始请求。否则在网络往返期间
            // （首次访问某英雄要拉国服 herodata）页面会继续渲染上个英雄的名字、
            // 头像、属性、技能，以及旧封面与旧视频。
            await OnUiThreadAsync(() =>
            {
                ResetLoadedContent();
                IsLoading = true;
                StatusMessage = "正在加载英雄资料…";
            }).ConfigureAwait(true);

            // 两段式加载：英雄列表通常命中本地缓存，先把基础信息渲染出来；
            // 详情资料（技能/背景/头图）首次需要联网，不能让它挡住整页。
            // 两个请求并行发出：串行的话详情资料要等英雄列表返回后才开始，
            // 首次访问某英雄会白等一个往返——转身速率就在资料里，会跟着一起迟到。
            var heroesTask = _heroService.GetHeroesAsync(token);
            var profileTask = _profileService.GetProfileAsync(heroId, token);
            ObserveSilently(profileTask);

            var heroes = await heroesTask.ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            if (generation != _loadGeneration)
            {
                return;
            }

            PickOptions = heroes;

            var found = heroes.FirstOrDefault(h => h.Id == heroId);
            if (found is null)
            {
                await OnUiThreadAsync(() =>
                {
                    StatusMessage = "未找到该英雄";
                    _loadedHeroId = null;
                }).ConfigureAwait(true);
                return;
            }

            await OnUiThreadAsync(() =>
            {
                Hero = found;
                // 下拉回显当前英雄；OnPickedHeroChanged 里 Id 相同会直接返回，不会重入加载
                PickedHero = found;
            }).ConfigureAwait(true);
            _ = WarmHeroIconsAsync(found, token, generation);

            var profile = await profileTask.ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            if (generation != _loadGeneration)
            {
                return;
            }

            await OnUiThreadAsync(() =>
            {
                if (token.IsCancellationRequested || generation != _loadGeneration)
                {
                    return;
                }

                ApplyProfile(profile);
                _loadedHeroId = found.Id;
                StatusMessage = string.Empty;
            }).ConfigureAwait(true);

            _ = WarmProfileIconsAsync(profile, token, generation);

            await LoadMatchupPreviewAsync(found.Id, token, generation).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested || generation != _loadGeneration)
        {
            _logger.Debug("详情加载已取消 HeroId={HeroId}", heroId);
        }
        catch (Exception ex) when (HttpCall.IsUserCancellation(ex, token) || generation != _loadGeneration)
        {
            _logger.Debug("详情加载已取消 HeroId={HeroId}", heroId);
        }
        catch (Exception ex)
        {
            if (generation != _loadGeneration)
            {
                return;
            }

            _logger.Error(ex, "加载英雄详情失败 HeroId={HeroId}", heroId);
            await OnUiThreadAsync(() =>
            {
                StatusMessage = "详情加载失败，请稍后重试";
                _loadedHeroId = null;
            }).ConfigureAwait(true);
        }
        finally
        {
            if (!token.IsCancellationRequested && generation == _loadGeneration)
            {
                await OnUiThreadAsync(() => IsLoading = false).ConfigureAwait(true);
            }
        }
    }

    /// <summary>
    /// 为并行发出的任务预注册静默观察者。
    /// 若因提前返回（未找到英雄 / 新的一次加载顶掉旧的）而无人 await 它，
    /// 其异常会以「未观察」的形式残留；取消时 GetProfileAsync 是会把异常抛出来的。
    /// 后续仍可正常 await——await 的异常抛出不受此 continuation 影响。
    /// </summary>
    private static void ObserveSilently(Task task)
        => _ = task.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    /// <summary>
    /// 预热横幅小图标：SmallIconUrl（icons/ 子路径）不在启动预热范围内，
    /// 冷路径依赖 WPF 懒下载且失败的实例会驻留内存缓存 —— 下载落盘后触发 Hero 重求值，
    /// 绑定重新走磁盘同步解码。
    /// </summary>
    private async Task WarmHeroIconsAsync(HeroStat hero, CancellationToken token, int generation)
    {
        try
        {
            await HeroImageCache.WarmUpAsync(
                [hero.IconUrl, hero.SmallIconUrl],
                decodeWidth: 64).ConfigureAwait(true);

            if (token.IsCancellationRequested || generation != _loadGeneration)
            {
                return;
            }

            await OnUiThreadAsync(() => OnPropertyChanged(nameof(Hero))).ConfigureAwait(true);
        }
        catch (Exception ex) when (HttpCall.IsUserCancellation(ex, token) || generation != _loadGeneration)
        {
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "预热英雄图标失败");
        }
    }

    /// <summary>
    /// 预热对位速览与克制列表的英雄图标：落盘后重放相关绑定，
    /// 让冷路径占位的图标走磁盘同步解码补图。
    /// </summary>
    private async Task WarmMatchupImagesAsync(HeroCounterResult result, CancellationToken token, int generation)
    {
        try
        {
            var urls = result.CounteredBy
                .Concat(result.Counters)
                .SelectMany(i => new[] { i.IconUrl, i.SmallIconUrl })
                .Concat((MatchupTopCountered ?? []).Concat(MatchupTopCounters ?? [])
                    .SelectMany(i => new[] { i.IconUrl, i.SmallIconUrl })))
                .Where(u => !string.IsNullOrWhiteSpace(u))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (urls.Count == 0)
            {
                return;
            }

            await HeroImageCache.WarmUpAsync(urls, decodeWidth: 96).ConfigureAwait(true);
            if (token.IsCancellationRequested || generation != _loadGeneration)
            {
                return;
            }

            await OnUiThreadAsync(() =>
            {
                if (token.IsCancellationRequested || generation != _loadGeneration)
                {
                    return;
                }

                OnPropertyChanged(nameof(MatchupTopCountered));
                OnPropertyChanged(nameof(MatchupTopCounters));
                OnPropertyChanged(nameof(CounteredByList));
                OnPropertyChanged(nameof(CountersList));
            }).ConfigureAwait(true);
        }
        catch (Exception ex) when (HttpCall.IsUserCancellation(ex, token) || generation != _loadGeneration)
        {
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "预热对位图标失败");
        }
    }

    /// <summary>预热技能图标并重建技能集合，让图标绑定重新求值走磁盘解码。</summary>
    private async Task WarmProfileIconsAsync(HeroDetailProfile? profile, CancellationToken token, int generation)
    {
        try
        {
            if (profile is null)
            {
                return;
            }

            var urls = profile.Abilities
                .Select(a => a.IconUrl)
                .Where(u => !string.IsNullOrWhiteSpace(u))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (urls.Count == 0)
            {
                return;
            }

            await HeroImageCache.WarmUpAsync(urls, decodeWidth: 128).ConfigureAwait(true);
            if (token.IsCancellationRequested || generation != _loadGeneration)
            {
                return;
            }

            await OnUiThreadAsync(() =>
            {
                if (token.IsCancellationRequested || generation != _loadGeneration)
                {
                    return;
                }

                // 集合重建让列表图标绑定重新求值；详情面板靠 SelectedAbility 通知重求值
                NormalAbilities.Clear();
                InnateAbilities.Clear();
                foreach (var ability in profile.Abilities)
                {
                    if (ability.IsInnate)
                    {
                        InnateAbilities.Add(ability);
                    }
                    else
                    {
                        NormalAbilities.Add(ability);
                    }
                }

                OnPropertyChanged(nameof(SelectedAbility));
            }).ConfigureAwait(true);
        }
        catch (Exception ex) when (HttpCall.IsUserCancellation(ex, token) || generation != _loadGeneration)
        {
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "预热技能图标失败");
        }
    }

    private static Task OnUiThreadAsync(Action action) => UiDispatcher.OnUiAsync(action);

    /// <summary>
    /// 清空上一个英雄的全部展示内容。切换英雄时必须在发起请求之前调用，
    /// 并且要连派生状态一起通知，否则集合虽已清空但页面仍按旧值渲染。
    /// </summary>
    partial void OnPickedHeroChanged(HeroStat? value)
    {
        // 从克制页签的下拉里直接换分析对象：整页跟随加载并停在克制页签。
        // Id 相同（可能是加载完成后的同一引用回填）时不重复加载。
        if (value is null || value.Id == Hero?.Id || value.Id == _loadedHeroId)
        {
            return;
        }

        _ = LoadAsync(value.Id);
    }

    private void ResetLoadedContent()
    {
        Hero = null;
        Profile = null;
        PickedHero = null;
        SelectedAbility = null;

        NormalAbilities.Clear();
        InnateAbilities.Clear();
        TalentRows.Clear();
        RoleStats.Clear();
        CounteredByList.Clear();
        CountersList.Clear();
        MatchupSeries = Array.Empty<ISeries>();
        _matchupChartItems = Array.Empty<HeroCounterItem>();
        MatchupTopCountered = [];
        MatchupTopCounters = [];
        OnPropertyChanged(nameof(HasMatchupTop));
        OnPropertyChanged(nameof(MatchupHasData));

        MatchupStatus = string.Empty;

        // 刻意不复位 SelectedTabIndex：克制列表行点击 / 页签内下拉换英雄时必须停留在
        // 「克制」页签，否则追溯动线每换一个英雄就被扔回总览（实测缺陷）。
        // 代价是从图鉴进入详情时会显示上次的页签——「记住位置」比硬复位更符合预期。

        OnPropertyChanged(nameof(HasInnateAbilities));
        OnPropertyChanged(nameof(HasTalents));
        OnPropertyChanged(nameof(HasMatchupPreview));
    }

    private void ApplyProfile(HeroDetailProfile? profile)
    {
        Profile = profile;
        NormalAbilities.Clear();
        InnateAbilities.Clear();
        TalentRows.Clear();
        RoleStats.Clear();

        if (profile is null)
        {
            SelectedAbility = null;
            if (Hero is not null)
            {
                foreach (var role in Hero.RoleStats)
                {
                    RoleStats.Add(role);
                }
            }

            return;
        }

        foreach (var ability in profile.Abilities)
        {
            if (ability.IsInnate)
            {
                InnateAbilities.Add(ability);
            }
            else
            {
                NormalAbilities.Add(ability);
            }
        }

        foreach (var row in profile.Talents)
        {
            TalentRows.Add(row);
        }

        SelectedAbility = NormalAbilities.FirstOrDefault()
            ?? InnateAbilities.FirstOrDefault();

        var levels = profile.RoleLevels;
        for (var i = 0; i < HeroDisplayHelper.StandardRoles.Count; i++)
        {
            var level = i < levels.Count ? Math.Clamp(levels[i], 0, 3) : 0;
            RoleStats.Add(new HeroRoleStat
            {
                Name = HeroDisplayHelper.StandardRoles[i],
                Score = level <= 0 ? 8 : level * 33.0
            });
        }

        OnPropertyChanged(nameof(HasInnateAbilities));
        OnPropertyChanged(nameof(HasTalents));
    }

    private async Task LoadMatchupPreviewAsync(int heroId, CancellationToken token, int generation)
    {
        try
        {
            await OnUiThreadAsync(() =>
            {
                IsLoadingMatchups = true;
                MatchupStatus = "正在加载对位数据...";
            }).ConfigureAwait(true);

            var result = await _heroService.GetCounterRelationsAsync(heroId, token).ConfigureAwait(true);
            if (token.IsCancellationRequested || generation != _loadGeneration)
            {
                return;
            }

            await OnUiThreadAsync(() =>
            {
                if (token.IsCancellationRequested || generation != _loadGeneration)
                {
                    return;
                }

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

                UpdateMatchupChart(result);
                MatchupTopCountered = result.CounteredBy.Take(3).ToList();
                MatchupTopCounters = result.Counters.Take(3).ToList();
                OnPropertyChanged(nameof(HasMatchupTop));
                OnPropertyChanged(nameof(MatchupHasData));

                if (result.IsEmpty)
                {
                    MatchupStatus = "暂无对位数据";
                }
                else if (result.IsOffline)
                {
                    MatchupStatus = "克制分析（离线缓存）";
                }
                else if (result.FromCache)
                {
                    MatchupStatus = "克制分析（缓存）";
                }
                else
                {
                    MatchupStatus = "克制分析";
                }

                OnPropertyChanged(nameof(HasMatchupPreview));

                // 对位图标预热：冷未命中在 UI 线程返回空位，落盘后重放绑定补图
                _ = WarmMatchupImagesAsync(result, token, generation);
            }).ConfigureAwait(true);
        }
        catch (Exception ex) when (HttpCall.IsUserCancellation(ex, token) || generation != _loadGeneration)
        {
            // ignore
        }
        catch (Exception ex)
        {
            if (generation != _loadGeneration)
            {
                return;
            }

            _logger.Warning(ex, "加载对位数据失败 HeroId={HeroId}", heroId);
            await OnUiThreadAsync(() => MatchupStatus = "对位预览加载失败").ConfigureAwait(true);
        }
        finally
        {
            if (!token.IsCancellationRequested && generation == _loadGeneration)
            {
                await OnUiThreadAsync(() => IsLoadingMatchups = false).ConfigureAwait(true);
            }
        }
    }

    [RelayCommand]
    private void SelectAbility(HeroAbilityInfo? ability)
    {
        if (ability is null)
        {
            return;
        }

        SelectedAbility = ability;
    }

    /// <summary>克制分析在长页「对位克制」分区，头部按钮滚动到该分区（页面监听事件）。</summary>
    public event Action? RequestScrollToMatchups;

    [RelayCommand]
    private void OpenCounter()
    {
        RequestScrollToMatchups?.Invoke();
    }

    /// <summary>
    /// 点击克制列表条目：在本页直接加载该英雄，并停在克制页签——
    /// 逐层追克制关系的动线不再跨页。
    /// </summary>
    [RelayCommand]
    private void OpenCounterForHero(HeroCounterItem? item)
    {
        if (item is null)
        {
            return;
        }

        _ = LoadAsync(item.HeroId);
    }

    /// <summary>克制页签的刷新：重跑当前英雄加载（英雄列表/资料/对位均走缓存，成本可控）。</summary>
    [RelayCommand]
    private void RefreshMatchups()
    {
        if (Hero is null)
        {
            return;
        }

        _ = LoadAsync(Hero.Id);
    }

    /// <summary>分布图的构图条目（柱 → 英雄，供点击跳转）。</summary>
    private IReadOnlyList<HeroCounterItem> _matchupChartItems = Array.Empty<HeroCounterItem>();

    /// <summary>横幅克制速览：最不利 / 最有利各前 3（对位数据就绪后填充）。</summary>
    [ObservableProperty]
    private IReadOnlyList<HeroCounterItem> matchupTopCountered = [];

    [ObservableProperty]
    private IReadOnlyList<HeroCounterItem> matchupTopCounters = [];

    public bool HasMatchupTop => MatchupTopCountered.Count > 0 || MatchupTopCounters.Count > 0;

    /// <summary>点击分布图柱子：打开该敌方英雄的克制分析（本页直接切换）。</summary>
    public void OpenMatchupChartHeroAt(int index)
    {
        if (index < 0 || index >= _matchupChartItems.Count)
        {
            return;
        }

        var item = _matchupChartItems[index];
        _ = LoadAsync(item.HeroId);
    }

    /// <summary>对位优势分布图：与原克制页同一构图（最劣势 6 + 最有利 6）。</summary>
    private void UpdateMatchupChart(HeroCounterResult result)
    {
        var items = result.CounteredBy.Take(6).Reverse()
            .Concat(result.Counters.Take(6))
            .ToList();

        if (items.Count == 0)
        {
            MatchupSeries = Array.Empty<ISeries>();
            MatchupXAxes = [new Axis { Labels = [] }];
            return;
        }

        MatchupSeries =
        [
            new ColumnSeries<double>
            {
                Name = "胜率差",
                Values = items.Select(x => x.WinRateDiff).ToArray(),
                Fill = new SolidColorPaint(SKColor.Parse("#F03D2E")),
                MaxBarWidth = 26
            }
        ];

        MatchupXAxes =
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

        _matchupChartItems = items;
    }

    [RelayCommand]
    private void BackToGallery()
    {
        _shell.CurrentPageTitle = "英雄图鉴";
        _shell.ActiveNav = MainWindowViewModel.HeroGalleryPageKey;
        _navigation.NavigateTo(MainWindowViewModel.HeroGalleryPageKey);
    }
}
