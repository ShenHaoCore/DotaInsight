using System.ComponentModel;
using System.Windows;
using DotaInsight.ViewModels.Dialogs;
using Wpf.Ui.Controls;

namespace DotaInsight.Views.Dialogs;

/// <summary>
/// 清理缓存弹窗的宿主窗口：只管对话框生命周期（关闭拦截、清理结果回传），
/// 清理流程与三态切换全部在 <see cref="ClearCacheDialogViewModel"/> 与内容视图里。
/// </summary>
public partial class ClearCacheDialogWindow : FluentWindow
{
    private readonly ClearCacheDialogViewModel _viewModel;

    public ClearCacheDialogWindow(ClearCacheDialogViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;

        viewModel.RequestClose += OnRequestClose;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    /// <summary>本次是否实际执行过清理（供 <see cref="Services.IDialogService"/> 回传）。</summary>
    public bool WasCleared => _viewModel.WasCleared;

    private void OnRequestClose() => Close();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // 清理跑在后台线程，进度回调还要往绑定属性上写：窗口先关会让它写到已关闭的视图上。
        // 清理至多数秒，直接拦住关闭更简单也更安全。
        if (_viewModel.State == ClearCacheDialogState.Progress)
        {
            e.Cancel = true;
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.RequestClose -= OnRequestClose;
        Closing -= OnClosing;
        Closed -= OnClosed;
    }
}
