using System.ComponentModel;
using System.Windows;
using DotaInsight.ViewModels.Dialogs;
using Wpf.Ui.Controls;

namespace DotaInsight.Views.Dialogs;

/// <summary>
/// 网络设置弹窗的宿主窗口：只管对话框生命周期与保存结果回传，
/// 校验 / 测试 / 保存逻辑都在 <see cref="NetworkSettingsDialogViewModel"/> 与内容视图里。
/// </summary>
public partial class NetworkSettingsDialogWindow : FluentWindow
{
    private readonly NetworkSettingsDialogViewModel _viewModel;

    public NetworkSettingsDialogWindow(NetworkSettingsDialogViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;

        viewModel.RequestClose += OnRequestClose;
        Closed += OnClosed;
    }

    /// <summary>本次是否保存过设置（供 <see cref="Services.IDialogService"/> 回传）。</summary>
    public bool WasSaved => _viewModel.WasSaved;

    private void OnRequestClose() => Close();

    private void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.RequestClose -= OnRequestClose;
        Closed -= OnClosed;
    }
}
