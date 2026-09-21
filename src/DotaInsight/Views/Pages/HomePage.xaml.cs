using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DotaInsight.ViewModels.Pages;

namespace DotaInsight.Views.Pages;

/// <summary>
/// 首页仪表盘。
/// </summary>
public partial class HomePage : Page
{
    private readonly HomeViewModel _viewModel;
    private bool _initialized;

    public HomePage(HomeViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        if (_viewModel.InitializeCommand.CanExecute(null))
        {
            await _viewModel.InitializeCommand.ExecuteAsync(null);
        }
    }

    private void OnAccountKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _viewModel.SearchPlayerCommand.CanExecute(null))
        {
            _viewModel.SearchPlayerCommand.Execute(null);
            e.Handled = true;
        }
    }
}

