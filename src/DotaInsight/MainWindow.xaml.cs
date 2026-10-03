using DotaInsight.Services;
using DotaInsight.ViewModels;
using DotaInsight.Views.Pages;
using Microsoft.Extensions.DependencyInjection;
using System.Windows;
using System.Windows.Threading;
using Wpf.Ui.Controls;

namespace DotaInsight;

/// <summary>
/// 主窗口：默认进入首页；空闲时预热其它页面以降低首次切换卡顿。
/// </summary>
public partial class MainWindow : FluentWindow
{
    private readonly IServiceProvider _services;
    private bool _warmedUp;

    public MainWindow(
        MainWindowViewModel viewModel,
        INavigationService navigationService,
        IServiceProvider services)
    {
        InitializeComponent();
        _services = services;
        DataContext = viewModel;
        navigationService.Initialize(PageHost);
        viewModel.NavigateHomeCommand.Execute(null);
        Loaded += OnLoadedWarmUp;
    }

    private void OnLoadedWarmUp(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedWarmUp;
        Dispatcher.BeginInvoke(WarmUpSecondaryPages, DispatcherPriority.ApplicationIdle);
    }

    private void WarmUpSecondaryPages()
    {
        if (_warmedUp)
        {
            return;
        }

        _warmedUp = true;
        try
        {
            // 单例页提前创建，避免第一次点侧栏时再解析 XAML
            _ = _services.GetRequiredService<HeroGalleryPage>();
            _ = _services.GetRequiredService<HeroCounterPage>();
            _ = _services.GetRequiredService<MatchAnalysisPage>();
            _ = _services.GetRequiredService<HeroDetailPage>();
        }
        catch
        {
            // 预热失败不影响主流程
        }
    }
}
