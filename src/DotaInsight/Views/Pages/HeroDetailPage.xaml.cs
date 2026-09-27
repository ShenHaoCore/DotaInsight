using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using DotaInsight.Helpers;
using DotaInsight.ViewModels.Pages;

namespace DotaInsight.Views.Pages;

/// <summary>
/// 英雄详情页：仅展示英雄 WebM 背景。
/// 视频状态机由 <see cref="HeroVideoController"/> 负责，本类只做事件转发。
/// </summary>
public partial class HeroDetailPage : Page
{
    private readonly HeroDetailViewModel _viewModel;
    private readonly HeroVideoController _video;

    public HeroDetailPage(HeroDetailViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        _video = new HeroVideoController(HeroVideoView, HeroPosterImage, ResolveMedia);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        HeroVideoView.Visibility = Visibility.Visible;
    }

    private (string? VideoUrl, string? PosterUrl) ResolveMedia() => (_viewModel.Profile?.VideoUrl, HeroDisplayHelper.FirstNonEmpty(_viewModel.Profile?.PosterUrl, _viewModel.Hero?.IconUrl));

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await _video.RefreshAsync().ConfigureAwait(true);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _viewModel.CancelPendingLoads();
        _video.OnUnloaded();
    }

    private async void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 开始加载新英雄时立刻清掉旧画面，避免残留上个英雄的视频
        if (e.PropertyName is nameof(HeroDetailViewModel.IsLoading) && _viewModel.IsLoading)
        {
            _video.PrepareForSwitch();
            return;
        }

        if (e.PropertyName is nameof(HeroDetailViewModel.Profile) or nameof(HeroDetailViewModel.Hero))
        {
            await _video.RefreshAsync().ConfigureAwait(true);
        }
    }
}
