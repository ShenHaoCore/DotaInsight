using System.Windows.Controls;
using DotaInsight.ViewModels.Pages;

namespace DotaInsight.Views.Pages;

/// <summary>
/// 比赛详情页：双方阵容与逐人数据。
/// </summary>
public partial class MatchDetailPage : Page
{
    public MatchDetailPage(MatchDetailViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
