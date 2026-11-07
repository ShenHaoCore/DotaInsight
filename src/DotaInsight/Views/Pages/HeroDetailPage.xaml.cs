using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DotaInsight.Helpers;
using DotaInsight.ViewModels.Pages;
using LiveChartsCore.Kernel;
using LiveChartsCore.Kernel.Sketches;

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
        viewModel.RequestScrollToMatchups += OnRequestScrollToMatchups;
        HeroVideoView.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// 头部「克制分析」按钮：切到对位克制页签并滚动到该卡片。
    /// 延后到 Loaded 优先级执行，确保目标卡片已完成布局、偏移计算有效。
    /// </summary>
    private void OnRequestScrollToMatchups()
    {
        _viewModel.MatchupTabIndex = 0;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            var offset = MatchupCard.TransformToVisual(DetailScroll).Transform(new Point(0, 0)).Y;
            DetailScroll.ScrollToVerticalOffset(Math.Max(0, offset - 8));
        }));
    }

    private (string? VideoUrl, string? PosterUrl) ResolveMedia() => (_viewModel.Profile?.VideoUrl, HeroDisplayHelper.FirstNonEmpty(_viewModel.Profile?.PosterUrl, _viewModel.Hero?.IconUrl));

    /// <summary>
    /// 克制页签分布图的柱子点击：转发给 VM，按柱序号打开对应敌方英雄的克制分析。
    /// DataPointerDown 的委托签名是 (IChartView, IEnumerable&lt;ChartPoint&gt;)，无 sender。
    /// </summary>
    private void OnMatchupChartPointerDown(IChartView chart, IEnumerable<ChartPoint> points)
    {
        if (points is null)
        {
            return;
        }

        var list = points.ToList();
        if (list.Count == 0)
        {
            return;
        }

        _viewModel.OpenMatchupChartHeroAt(list[0].Index);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsLoading)
        {
            // 正在切换英雄：此刻 Profile 已被 VM 清空，刷新只会拿到空媒体并白跑一次
            // WebView2 初始化；保持空白，等新资料到位后由 Profile 变更驱动刷新。
            _video.PrepareForSwitch();
            ResetScroll();
            return;
        }

        await _video.RefreshAsync().ConfigureAwait(true);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _viewModel.CancelPendingLoads();
        _video.OnUnloaded();
    }

    /// <summary>
    /// 主体滚动联动：banner 里的视频滚出视野时冻结为封面，回到顶部再恢复播放。
    /// </summary>
    private void OnDetailScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        _ = _video.OnScrollAsync(e.VerticalOffset, e.VerticalChange);
    }

    /// <summary>
    /// 头图区尺寸变化时同步圆角裁切矩形（WebView2 合成控件不吃 WPF 圆角，
    /// 必须靠上层 Grid.Clip 硬裁，而 Clip 不随容器自动缩放）。
    /// </summary>
    private void OnPosterFrameSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not Border frame)
        {
            return;
        }

        var width = frame.ActualWidth > 0 ? frame.ActualWidth : e.NewSize.Width;
        var height = frame.ActualHeight > 0 ? frame.ActualHeight : e.NewSize.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        PosterClip.Rect = new Rect(0, 0, width, height);
    }

    private async void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 开始加载新英雄时立刻清掉旧画面，避免残留上个英雄的视频与封面
        if (e.PropertyName is nameof(HeroDetailViewModel.IsLoading) && _viewModel.IsLoading)
        {
            _video.PrepareForSwitch();
            ResetScroll();
            return;
        }

        if (e.PropertyName is nameof(HeroDetailViewModel.Profile) or nameof(HeroDetailViewModel.Hero))
        {
            await _video.RefreshAsync().ConfigureAwait(true);
        }
    }

    /// <summary>
    /// 换英雄时把滚动位置复位：页面实例被导航服务缓存复用，
    /// ScrollViewer 的偏移会残留到下一个英雄。延后到 Loaded 优先级执行，
    /// 确保布局（含偏移恢复）已完成，否则会被随后的布局覆盖。
    /// </summary>
    private void ResetScroll()
        => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(DetailScroll.ScrollToTop));
}
