using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DotaInsight.Models;
using DotaInsight.ViewModels.Pages;

namespace DotaInsight.Views.Pages;

/// <summary>
/// 战绩分析页。
/// </summary>
public partial class MatchAnalysisPage : Page
{
    private readonly MatchAnalysisViewModel _viewModel;

    public MatchAnalysisPage(MatchAnalysisViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _viewModel.SearchCommand.CanExecute(null))
        {
            _viewModel.SearchCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>点击比赛行 → 打开该场比赛详情。选中态立即清空，便于重复点击同一场。</summary>
    private void OnMatchSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox { SelectedItem: RecentMatchItem match } list)
        {
            return;
        }

        list.SelectedItem = null;
        _viewModel.OpenMatchCommand.Execute(match);
    }

    /// <summary>
    /// 热力图悬浮：按鼠标坐标反查日历格（列=周、行=星期），动态设置共享 ToolTip。
    /// 单张位图渲染没有逐格元素，tooltip 由这里代管。
    /// </summary>
    private void OnActivityImageMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not Image image
            || _viewModel.ActivityCells.Count == 0
            || image.Source is not { } source)
        {
            return;
        }

        // 鼠标坐标 → 位图像素坐标（元素尺寸可能被布局拉伸，按位图实际尺寸折算）
        var pos = e.GetPosition(image);
        var px = pos.X * source.Width / Math.Max(1, image.ActualWidth);
        var py = pos.Y * source.Height / Math.Max(1, image.ActualHeight);

        var cell = _viewModel.ActivityCellSize;
        var gap = Helpers.ActivityCalendarRenderer.GapSize;
        var stride = cell + gap;

        var col = (int)Math.Floor((px - gap / 2) / stride);
        var row = (int)Math.Floor((py - gap / 2) / stride);

        string? tooltip = null;
        if (col >= 0 && col < 53 && row >= 0 && row < 7)
        {
            var index = col * 7 + row;
            if (index < _viewModel.ActivityCells.Count)
            {
                var day = _viewModel.ActivityCells[index];
                if (day.Level >= 0)
                {
                    tooltip = day.Tooltip;
                }
            }
        }

        // 仅在变化时赋值，避免每个 MouseMove 都重建 ToolTip
        if (!Equals(image.ToolTip, tooltip))
        {
            image.ToolTip = tooltip;
        }
    }

    private void OnActivityImageMouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Image image)
        {
            image.ToolTip = null;
        }
    }

    /// <summary>热力图容器宽度变化：格子自适应铺满可用宽度并重渲位图。</summary>
    private void OnActivitySizeChanged(object sender, SizeChangedEventArgs e)
        => _viewModel.RebuildActivityImage(e.NewSize.Width);
}
