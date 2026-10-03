using System.Windows;

namespace DotaInsight.Helpers;

/// <summary>
/// 把 UI 状态更新切回 UI 线程执行的公共入口。
/// 服务层内部使用 ConfigureAwait(false)，await 之后的续体线程并不保证是 UI 线程；
/// 若直接在续体里改绑定属性，PropertyChanged 会在后台线程抛出，
/// 页面里的封面 / 视频控件会因跨线程访问而崩溃。
/// </summary>
internal static class UiDispatcher
{
    /// <summary>已在 UI 线程则同步执行，否则经 Dispatcher 调度。</summary>
    public static Task OnUiAsync(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(action).Task;
    }
}
