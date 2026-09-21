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
        Abilities = new ObservableCollection<HeroAbilityInfo>();
    }

    public ObservableCollection<HeroCounterItem> CounteredByPreview { get; }

    public ObservableCollection<HeroCounterItem> CountersPreview { get; }

    public ObservableCollection<HeroRoleStat> RoleStats { get; }

    public ObservableCollection<HeroAbilityInfo> Abilities { get; }

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

    public string BioToggleText => IsBioExpanded ? "收起" : "展开全文";

    public bool HasHero => Hero is not null;

    public bool HasProfile => Profile is not null;

    public bool HasVideo => !string.IsNullOrWhiteSpace(Profile?.VideoUrl);

    public bool HasHype => !string.IsNullOrWhiteSpace(Profile?.Hype);

    public bool HasNpeDesc => !string.IsNullOrWhiteSpace(Profile?.NpeDesc);

    public bool HasBio => !string.IsNullOrWhiteSpace(Profile?.Bio);

    public bool HasAbilities => Abilities.Count > 0;

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
            if (_loadedHeroId == heroId && Hero is not null)
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

    private async Task LoadFromStatAsync(HeroStat stat)
        => await LoadAsync(stat.Id).ConfigureAwait(true);

    [RelayCommand]
    private async Task LoadAsync(int heroId)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        try
        {
            IsLoading = true;
            StatusMessage = "加载详情...";
            CounteredByPreview.Clear();
            CountersPreview.Clear();
            MatchupStatus = string.Empty;
            IsBioExpanded = false;
            OnPropertyChanged(nameof(HasMatchupPreview));

            var heroesTask = _heroService.GetHeroesAsync(token);
            var profileTask = _profileService.GetProfileAsync(heroId, token);
            await Task.WhenAll(heroesTask, profileTask).ConfigureAwait(true);

            var found = (await heroesTask).FirstOrDefault(h => h.Id == heroId);
            var profile = await profileTask;

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                Hero = found;
                ApplyProfile(profile);
                _loadedHeroId = found?.Id;
                StatusMessage = found is null ? "未找到该英雄" : string.Empty;
                OnPropertyChanged(nameof(HasBrackets));
            });

            if (found is null || token.IsCancellationRequested)
            {
                return;
            }

            await LoadMatchupPreviewAsync(found.Id, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            _logger.Debug("详情加载已取消 HeroId={HeroId}", heroId);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "加载英雄详情失败 HeroId={HeroId}", heroId);
            StatusMessage = "详情加载失败";
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                IsLoading = false;
            }
        }
    }

    private void ApplyProfile(HeroDetailProfile? profile)
    {
        Profile = profile;
        Abilities.Clear();
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
            Abilities.Add(ability);
        }

        SelectedAbility = Abilities.FirstOrDefault();

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
        OnPropertyChanged(nameof(HasSelectedAbility));
    }

    private async Task LoadMatchupPreviewAsync(int heroId, CancellationToken token)
    {
        try
        {
            IsLoadingMatchups = true;
            MatchupStatus = "加载对位预览...";
            var result = await _heroService.GetCounterRelationsAsync(heroId, token).ConfigureAwait(true);
            if (token.IsCancellationRequested)
            {
                return;
            }

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
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

                MatchupStatus = result.IsOffline
                    ? "对位预览（离线缓存）"
                    : result.FromCache
                        ? "对位预览（缓存）"
                        : "对位预览";
                OnPropertyChanged(nameof(HasMatchupPreview));
            });
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "加载对位预览失败 HeroId={HeroId}", heroId);
            MatchupStatus = "对位预览加载失败";
        }
        finally
        {
            if (!token.IsCancellationRequested)
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
