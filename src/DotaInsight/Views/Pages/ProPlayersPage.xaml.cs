using System.Windows.Controls;
using DotaInsight.ViewModels.Pages;

namespace DotaInsight.Views.Pages;

/// <summary>
/// 职业选手榜单页：选手榜 / 战队榜。
/// </summary>
public partial class ProPlayersPage : Page
{
    public ProPlayersPage(ProPlayersViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
