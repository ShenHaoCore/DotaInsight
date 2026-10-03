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
}
