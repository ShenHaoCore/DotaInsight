using System.Windows;
using System.Windows.Controls;
using DotaInsight.Models;
using DotaInsight.ViewModels.Pages;
using Wpf.Ui.Controls;

namespace DotaInsight.Views.Pages;

/// <summary>
/// 英雄图鉴页。
/// </summary>
public partial class HeroGalleryPage : Page
{
    private readonly HeroGalleryViewModel _viewModel;
    private bool _initialized;

    public HeroGalleryPage(HeroGalleryViewModel viewModel)
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

    private void OnSuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is HeroStat hero)
        {
            _viewModel.OpenDetailCommand.Execute(hero);
        }
    }
}
