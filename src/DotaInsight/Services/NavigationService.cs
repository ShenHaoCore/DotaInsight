using System.Windows;

using System.Windows.Controls;

using Microsoft.Extensions.DependencyInjection;



namespace DotaInsight.Services;



/// <summary>

/// 页面接收导航参数。

/// </summary>

public interface INavigationAware

{

    void OnNavigatedTo(object? parameter);

}



/// <summary>

/// 简单页面导航服务：在 Frame 中切换 Page。

/// </summary>

public interface INavigationService

{

    void Initialize(Frame host);



    void NavigateTo(string pageKey, object? parameter = null);

}



/// <summary>

/// 基于 DI 的页面工厂导航；主功能页实例缓存，避免反复创建导致卡顿。

/// </summary>

public sealed class NavigationService : INavigationService

{

    private readonly IServiceProvider _services;

    private readonly Dictionary<string, Type> _pageMap;

    private readonly Dictionary<string, object> _pageCache;

    private Frame? _host;

    private string? _currentKey;



    public NavigationService(IServiceProvider services)

    {

        _services = services;

        _pageMap = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);

        _pageCache = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

    }



    public void Register(string key, Type pageType)

    {

        _pageMap[key] = pageType;

    }



    public void Initialize(Frame host)

    {

        _host = host;

    }



    public void NavigateTo(string pageKey, object? parameter = null)

    {

        if (_host is null)

        {

            throw new InvalidOperationException("导航宿主尚未初始化。");

        }



        if (!_pageMap.TryGetValue(pageKey, out var pageType))

        {

            throw new InvalidOperationException($"未注册页面：{pageKey}");

        }



        if (!_pageCache.TryGetValue(pageKey, out var page))

        {

            page = _services.GetRequiredService(pageType);

            _pageCache[pageKey] = page;

        }



        if (page is FrameworkElement { DataContext: INavigationAware awareVm })

        {

            awareVm.OnNavigatedTo(parameter);

        }

        else if (page is INavigationAware awarePage)

        {

            awarePage.OnNavigatedTo(parameter);

        }



        // 同一页且无参数：不重复切换，避免闪烁

        if (string.Equals(_currentKey, pageKey, StringComparison.OrdinalIgnoreCase)

            && parameter is null

            && ReferenceEquals(_host.Content, page))

        {

            return;

        }



        // 使用 Content 赋值而非 Navigate，避免日志堆栈与重复实例

        _host.Content = page;

        _currentKey = pageKey;



        // 清理可能残留的导航日志，降低内存压力

        while (_host.CanGoBack)

        {

            _host.RemoveBackEntry();

        }

    }

}


