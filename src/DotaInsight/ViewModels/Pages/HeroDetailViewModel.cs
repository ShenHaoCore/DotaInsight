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
        CounteredByPreview = new ObservableCollection<HeroCounterItem>();
        CountersPreview = new ObservableCollection<HeroCounterItem>();
        RoleStats = new ObservableCollection<HeroRoleStat>();
        NormalAbilities = new ObservableCollection<HeroAbilityInfo>();
        InnateAbilities = new ObservableCollection<HeroAbilityInfo>();
        TalentRows = new ObservableCollection<HeroTalentRow>();
    }

    public ObservableCollection<HeroCounterItem> CounteredByPreview { get; }

    public ObservableCollection<HeroCounterItem> CountersPreview { get; }

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
    [NotifyPropertyChangedFor(nameof(HasVideo))]
    [NotifyPropertyChangedFor(nameof(HasHype))]
    [NotifyPropertyChangedFor(nameof(HasNpeDesc))]
    [NotifyPropertyChangedFor(nameof(HasBio))]
    [NotifyPropertyChangedFor(nameof(HasAbilities))]
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
    [NotifyPropertyChangedFor(nameof(HasSelectedAbility))]
    private HeroAbilityInfo? selectedAbility;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BioToggleText))]
    private bool isBioExpanded;

    [ObservableProperty]
    private int selectedTabIndex;

    public string BioToggleText => IsBioExpanded ? "收起" : "展开全文";

    public bool HasHero => Hero is not null;

    public bool HasProfile => Profile is not null;

    public bool HasVideo => !string.IsNullOrWhiteSpace(Profile?.VideoUrl);

    public bool HasHype => !string.IsNullOrWhiteSpace(Profile?.Hype);

    public bool HasNpeDesc => !string.IsNullOrWhiteSpace(Profile?.NpeDesc);

    public bool HasBio => !string.IsNullOrWhiteSpace(Profile?.Bio);

    public bool HasAbilities => NormalAbilities.Count > 0;

    public bool HasInnateAbilities => InnateAbilities.Count > 0;

    public bool HasTalents => TalentRows.Count > 0;

    public bool HasSelectedAbility => SelectedAbility is not null;

    public int ComplexityLevel => Profile?.Complexity ?? 0;

    public bool HasComplexity1 => ComplexityLevel >= 1;

    public bool HasComplexity2 => ComplexityLevel >= 2;

    public bool HasComplexity3 => ComplexityLevel >= 3;

    public bool HasBrackets => Hero?.Brackets is { Count: > 0 };

    public bool HasMatchupPreview => CounteredByPreview.Count > 0 || CountersPreview.Count > 0;

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

            await OnUiThreadAsync(() => Hero = found).ConfigureAwait(true);

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
    /// 把 UI 状态更新切回 UI 线程执行。
    /// 服务层内部使用 ConfigureAwait(false)，await 之后的续体线程并不保证是 UI 线程；
    /// 若直接在续体里改绑定属性，PropertyChanged 会在后台线程抛出，
    /// 页面的封面 / 视频控件随即因跨线程访问而崩溃。
    /// </summary>
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

    /// <summary>
    /// 清空上一个英雄的全部展示内容。切换英雄时必须在发起请求之前调用，
    /// 并且要连派生状态一起通知，否则集合虽已清空但页面仍按旧值渲染。
    /// </summary>
    private void ResetLoadedContent()
    {
        Hero = null;
        Profile = null;
        SelectedAbility = null;

        NormalAbilities.Clear();
        InnateAbilities.Clear();
        TalentRows.Clear();
        RoleStats.Clear();
        CounteredByPreview.Clear();
        CountersPreview.Clear();

        MatchupStatus = string.Empty;
        IsBioExpanded = false;
        SelectedTabIndex = 0;

        OnPropertyChanged(nameof(HasAbilities));
        OnPropertyChanged(nameof(HasInnateAbilities));
        OnPropertyChanged(nameof(HasTalents));
        OnPropertyChanged(nameof(HasSelectedAbility));
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

        OnPropertyChanged(nameof(HasAbilities));
        OnPropertyChanged(nameof(HasInnateAbilities));
        OnPropertyChanged(nameof(HasTalents));
        OnPropertyChanged(nameof(HasSelectedAbility));
    }

    private async Task LoadMatchupPreviewAsync(int heroId, CancellationToken token, int generation)
    {
        try
        {
            await OnUiThreadAsync(() =>
            {
                IsLoadingMatchups = true;
                MatchupStatus = "加载对位预览...";
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

                CounteredByPreview.Clear();
                foreach (var item in result.CounteredBy.Take(5))
                {
                    CounteredByPreview.Add(item);
                }

                CountersPreview.Clear();
                foreach (var item in result.Counters.Take(5))
                {
                    CountersPreview.Add(item);
                }

                if (result.IsEmpty)
                {
                    MatchupStatus = "对位预览暂无数据";
                }
                else if (result.IsOffline)
                {
                    MatchupStatus = "对位预览（离线缓存）";
                }
                else if (result.FromCache)
                {
                    MatchupStatus = "对位预览（缓存）";
                }
                else
                {
                    MatchupStatus = "对位预览";
                }

                OnPropertyChanged(nameof(HasMatchupPreview));
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

            _logger.Warning(ex, "加载对位预览失败 HeroId={HeroId}", heroId);
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

    [RelayCommand]
    private void ToggleBio() => IsBioExpanded = !IsBioExpanded;

    [RelayCommand]
    private void OpenCounter()
    {
        if (Hero is null)
        {
            return;
        }

        NavigateToCounter(Hero.Id);
    }

    /// <summary>点击对位列表条目：跳到该英雄自己的克制分析页。</summary>
    [RelayCommand]
    private void OpenCounterForHero(HeroCounterItem? item)
    {
        if (item is null)
        {
            return;
        }

        NavigateToCounter(item.HeroId);
    }

    private void NavigateToCounter(int heroId)
    {
        _shell.CurrentPageTitle = "克制分析";
        _shell.ActiveNav = MainWindowViewModel.HeroCounterPageKey;
        _navigation.NavigateTo(MainWindowViewModel.HeroCounterPageKey, heroId);
    }

    [RelayCommand]
    private void BackToGallery()
    {
        _shell.CurrentPageTitle = "英雄图鉴";
        _shell.ActiveNav = MainWindowViewModel.HeroGalleryPageKey;
        _navigation.NavigateTo(MainWindowViewModel.HeroGalleryPageKey);
    }
}
