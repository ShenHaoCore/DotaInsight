using System.Windows.Controls;

namespace DotaInsight.Views.Dialogs;

/// <summary>
/// 网络设置弹窗内容：纯视图，全部逻辑在 <see cref="ViewModels.Dialogs.NetworkSettingsDialogViewModel"/>。
/// </summary>
public partial class NetworkSettingsDialog : UserControl
{
    public NetworkSettingsDialog()
    {
        InitializeComponent();
    }
}
