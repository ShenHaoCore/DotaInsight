using System.Runtime.InteropServices;

namespace DotaInsight.Helpers;

/// <summary>
/// 单实例保护：同一 Windows 会话内只允许一个 DotaInsight 进程。
/// 双实例的实害：LiteDB 用 Connection=shared 能扛住，但 WebView2 用户数据目录
/// （%LocalAppData%\DotaInsight\webview2）是排他锁——第二个实例的英雄视频区会静默失效，
/// 用户还会面对两个内容不同步的窗口。
/// 用 <c>Local\</c> 命名空间：同一会话内唯一，不同用户会话（快速用户切换）互不影响。
/// </summary>
public static class SingleInstance
{
    public const string MutexName = @"Local\DotaInsight.SingleInstance";

    /// <summary>
    /// 尝试成为本会话的第一个实例。
    /// 成功时返回 true，<paramref name="guard"/> 持有互斥体直到 Dispose（须在 owning 线程上释放）；
    /// 已有实例时返回 false。<paramref name="mutexName"/> 供测试注入唯一名，
    /// 避免与运行中的应用互斥体撞名。
    /// </summary>
    public static bool TryAcquireFirst(out IDisposable? guard, string? mutexName = null)
    {
        var mutex = new Mutex(initiallyOwned: true, mutexName ?? MutexName, out var createdNew);
        if (createdNew)
        {
            guard = new MutexGuard(mutex);
            return true;
        }

        // 已有实例在运行：立即交还句柄，由调用方退出
        mutex.Dispose();
        guard = null;
        return false;
    }

    /// <summary>
    /// 把已运行的实例拉到前台（双击 exe 时用户预期是「窗口出来」而不是「毫无反应」）。
    /// 按主窗口标题查找；找不到（如标题被改动）时静默放弃，仅保证不产生第二个实例。
    /// </summary>
    public static bool TryActivateExisting(string windowTitle)
    {
        if (string.IsNullOrWhiteSpace(windowTitle))
        {
            return false;
        }

        try
        {
            var hwnd = FindWindowW(null, windowTitle);
            if (hwnd == IntPtr.Zero)
            {
                return false;
            }

            if (IsIconic(hwnd))
            {
                ShowWindow(hwnd, SwRestore);
            }

            return SetForegroundWindow(hwnd);
        }
        catch
        {
            // 激活失败不影响「退出第二实例」的主流程
            return false;
        }
    }

    private const int SwRestore = 9;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindowW(string? lpClassName, string lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    /// <summary>Mutex 释放有线程亲和性：必须在获取它的线程（App 的 UI 线程）上 Dispose。</summary>
    private sealed class MutexGuard(Mutex mutex) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // 当前线程不拥有互斥体（异常退出路径）
            }
            finally
            {
                mutex.Dispose();
            }
        }
    }
}
