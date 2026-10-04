using System.Windows;
using DotaInsight.Views.Dialogs;
using Microsoft.Extensions.DependencyInjection;

namespace DotaInsight.Services;

/// <summary>
/// 弹窗显示入口：把 View 的创建与生命周期留在服务层，
/// ViewModel 只关心「弹出来没、到底清没清」，不必自己 new 窗口。
/// </summary>
public interface IDialogService
{
    /// <summary>显示「清理缓存」弹窗，返回是否实际执行过清理。</summary>
    bool ShowClearCacheDialog();
}

public sealed class DialogService : IDialogService
{
    private readonly IServiceProvider _services;

    public DialogService(IServiceProvider services) => _services = services;

    public bool ShowClearCacheDialog()
    {
        var dialog = _services.GetRequiredService<ClearCacheDialogWindow>();
        var owner = Application.Current?.MainWindow;

        if (owner is not null && !ReferenceEquals(owner, dialog))
        {
            dialog.Owner = owner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        dialog.ShowDialog();
        return dialog.WasCleared;
    }
}
