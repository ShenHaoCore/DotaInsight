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

    [ObservableProperty]
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
            IsLoading = true;
            StatusMessage = "加载详情...";
            CounteredByPreview.Clear();
            CountersPreview.Clear();
            MatchupStatus = string.Empty;
            IsBioExpanded = false;
            SelectedTabIndex = 0;
            OnPropertyChanged(nameof(HasMatchupPreview));

            var heroesTask = _heroService.GetHeroesAsync(token);
            var profileTask = _profileService.GetProfileAsync(heroId, token);
            await Task.WhenAll(heroesTask, profileTask).ConfigureAwait(true);

            token.ThrowIfCancellationRequested();
            if (generation != _loadGeneration)
            {
                return;
            }

            var found = (await heroesTask).FirstOrDefault(h => h.Id == heroId);
            var profile = await profileTask;

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested || generation != _loadGeneration)
                {
                    return;
                }

                Hero = found;
                ApplyProfile(profile);
                _loadedHeroId = found?.Id;
                StatusMessage = found is null ? "未找到该英雄" : string.Empty;
                OnPropertyChanged(nameof(HasBrackets));
            });

            if (found is null || token.IsCancellationRequested || generation != _loadGeneration)
            {
                return;
            }

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
            StatusMessage = "详情加载失败，请稍后重试";
            _loadedHeroId = null;
        }
        finally
        {
            if (!token.IsCancellationRequested && generation == _loadGeneration)
            {
                IsLoading = false;
            }
        }
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
            IsLoadingMatchups = true;
            MatchupStatus = "加载对位预览...";
            var result = await _heroService.GetCounterRelationsAsync(heroId, token).ConfigureAwait(true);
            if (token.IsCancellationRequested || generation != _loadGeneration)
            {
                return;
            }

            await Application.Current.Dispatcher.InvokeAsync(() =>
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
            });
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
            MatchupStatus = "对位预览加载失败";
        }
        finally
        {
            if (!token.IsCancellationRequested && generation == _loadGeneration)
            {
                IsLoadingMatchups = false;
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

        _shell.CurrentPageTitle = "克制分析";
        _shell.ActiveNav = MainWindowViewModel.HeroCounterPageKey;
        _navigation.NavigateTo(MainWindowViewModel.HeroCounterPageKey, Hero.Id);
    }

    [RelayCommand]
    private void BackToGallery()
    {
        _shell.CurrentPageTitle = "英雄图鉴";
        _shell.ActiveNav = MainWindowViewModel.HeroGalleryPageKey;
        _navigation.NavigateTo(MainWindowViewModel.HeroGalleryPageKey);
    }
}
