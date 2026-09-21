using System.Windows.Controls;
using System.Windows.Input;
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
}
