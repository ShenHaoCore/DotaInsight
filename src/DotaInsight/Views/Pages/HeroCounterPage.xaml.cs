using System.Windows.Controls;
using DotaInsight.Models;
using DotaInsight.ViewModels.Pages;
using Wpf.Ui.Controls;

namespace DotaInsight.Views.Pages;

/// <summary>
/// 英雄克制分析页。
/// </summary>
public partial class HeroCounterPage : Page
{
    private readonly HeroCounterViewModel _viewModel;

    public HeroCounterPage(HeroCounterViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;
    }

    private void OnHeroSuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is HeroStat hero)
        {
            _viewModel.SelectedHero = hero;
            sender.Text = hero.DisplayName;
        }
    }
}
