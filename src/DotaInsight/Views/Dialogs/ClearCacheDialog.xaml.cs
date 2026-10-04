using System.Windows.Controls;

namespace DotaInsight.Views.Dialogs;

/// <summary>
/// 清理缓存弹窗的内容视图：确认 / 进度 / 结果三态由
/// <see cref="ViewModels.Dialogs.ClearCacheDialogViewModel"/> 驱动，自身不含逻辑。
/// </summary>
public partial class ClearCacheDialog : UserControl
{
    public ClearCacheDialog() => InitializeComponent();
}
