using System.Windows;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace DotaInsight.Services;

/// <summary>
/// 应用主题：默认同步系统浅色/深色，并套用 DotaInsight 自定义色板。
/// </summary>
public interface IThemeService
{
    bool IsDark { get; }

    /// <summary>当前 Windows「应用模式」是否为深色。</summary>
    bool IsSystemDark { get; }

    void ApplyTheme(bool isDark);

    /// <summary>按系统主题应用。</summary>
    void ApplyFollowSystem();

    void Toggle();
}

public sealed class ThemeService : IThemeService
{
    private const string DarkThemeUri = "Themes/Dark.xaml";
    private const string LightThemeUri = "Themes/Light.xaml";

    public bool IsDark { get; private set; }

    public bool IsSystemDark => ReadSystemPrefersDark();

    public void ApplyFollowSystem() => ApplyTheme(IsSystemDark);

    public void ApplyTheme(bool isDark)
    {
        IsDark = isDark;
        var appTheme = isDark ? ApplicationTheme.Dark : ApplicationTheme.Light;

        ApplicationThemeManager.Apply(appTheme, WindowBackdropType.None, updateAccent: true);
        SwapAppThemeDictionary(isDark);

        if (Application.Current?.MainWindow is FrameworkElement root)
        {
            ApplicationThemeManager.Apply(root);
        }
    }

    public void Toggle() => ApplyTheme(!IsDark);

    /// <summary>
    /// 读取 Windows 设置 → 个性化 → 颜色 → 选择默认应用模式。
    /// AppsUseLightTheme: 1=浅色，0=深色。
    /// </summary>
    private static bool ReadSystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            if (value is int i)
            {
                return i == 0;
            }

            if (value is long l)
            {
                return l == 0;
            }
        }
        catch
        {
            // 无法读取时默认深色
        }

        return true;
    }

    private static void SwapAppThemeDictionary(bool isDark)
    {
        var app = Application.Current;
        if (app is null)
        {
            return;
        }

        var targetUri = new Uri(isDark ? DarkThemeUri : LightThemeUri, UriKind.Relative);
        var dicts = app.Resources.MergedDictionaries;

        ResourceDictionary? existing = null;
        var insertIndex = -1;

        for (var i = 0; i < dicts.Count; i++)
        {
            var src = dicts[i].Source?.OriginalString ?? string.Empty;
            if (src.Contains("Themes/Dark.xaml", StringComparison.OrdinalIgnoreCase)
                || src.Contains("Themes/Light.xaml", StringComparison.OrdinalIgnoreCase)
                || src.Contains("Themes/DotaInsightTheme.xaml", StringComparison.OrdinalIgnoreCase))
            {
                existing = dicts[i];
                insertIndex = i;
                break;
            }
        }

        var next = new ResourceDictionary { Source = targetUri };

        if (existing is not null && insertIndex >= 0)
        {
            dicts.RemoveAt(insertIndex);
            dicts.Insert(insertIndex, next);
        }
        else
        {
            dicts.Insert(Math.Min(2, dicts.Count), next);
        }
    }
}

