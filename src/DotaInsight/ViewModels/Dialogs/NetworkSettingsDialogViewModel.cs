using System.Diagnostics;
using System.Net;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotaInsight.Helpers;
using DotaInsight.Models;
using DotaInsight.Services;
using Serilog;

namespace DotaInsight.ViewModels.Dialogs;

/// <summary>
/// 网络设置弹窗：代理地址的查看 / 校验 / 连通性测试 / 保存。
/// 保存即写盘并广播（<see cref="INetworkSettingsService"/>），下一个请求就走新通道。
/// </summary>
public sealed partial class NetworkSettingsDialogViewModel : ObservableObject
{
    private readonly INetworkSettingsService _settings;
    private readonly ILogger _logger;

    public NetworkSettingsDialogViewModel(INetworkSettingsService settings, ILogger logger)
    {
        _settings = settings;
        _logger = logger.ForContext<NetworkSettingsDialogViewModel>();
        proxyUrlText = _settings.Current.ProxyUrl;
    }

    /// <summary>请求宿主窗口关闭（保存成功或用户点取消）。</summary>
    public event Action? RequestClose;

    /// <summary>本次是否保存过设置（供 <see cref="Services.IDialogService"/> 回传）。</summary>
    public bool WasSaved { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ValidationError))]
    [NotifyPropertyChangedFor(nameof(HasValidationError))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string proxyUrlText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private bool isTesting;

    [ObservableProperty]
    private string testResultText = string.Empty;

    /// <summary>输入校验：空 = 直连（合法）；非空必须是 http/https/socks5 绝对地址。</summary>
    public string? ValidationError
        => string.IsNullOrWhiteSpace(ProxyUrlText)
            ? null
            : NetworkSettings.TryParseProxyUrl(ProxyUrlText, out _)
                ? null
                : "地址无效：需要 http://host:port 或 socks5://host:port";

    public bool HasValidationError => ValidationError is not null;

    public bool CanSave => !HasValidationError && !IsTesting;

    public bool HasTestResult => !string.IsNullOrEmpty(TestResultText);

    [RelayCommand]
    private void Save()
    {
        if (!CanSave)
        {
            return;
        }

        _settings.Save(new NetworkSettings { ProxyUrl = ProxyUrlText.Trim() });
        WasSaved = true;
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void Dismiss() => RequestClose?.Invoke();

    [RelayCommand]
    private async Task TestAsync()
    {
        if (IsTesting)
        {
            return;
        }

        IsTesting = true;
        TestResultText = "正在测试连通…";

        try
        {
            // 测试通道按输入框的当前值建，不依赖已保存设置——改完没存也能先试
            NetworkSettings.TryParseProxyUrl(ProxyUrlText, out var proxy);
            using var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(1)
            };
            if (proxy is not null)
            {
                handler.Proxy = proxy;
            }

            using var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(8),
                BaseAddress = new Uri(ApiEndpoints.OpenDotaBase)
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("DotaInsight/1.0");

            var sw = Stopwatch.StartNew();
            var resp = await client.GetAsync("api/heroes");
            sw.Stop();
            TestResultText = ((int)resp.StatusCode) switch
            {
                200 => $"✓ 连接成功，耗时 {sw.ElapsedMilliseconds}ms",
                _ => $"⚠ 服务端返回 {(int)resp.StatusCode}（{sw.ElapsedMilliseconds}ms），代理链路本身已通"
            };
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "代理连通性测试失败");
            var reason = ex.InnerException?.Message ?? ex.Message;
            TestResultText = $"✗ 连接失败：{reason}";
        }
        finally
        {
            IsTesting = false;
            OnPropertyChanged(nameof(HasTestResult));
        }
    }
}
