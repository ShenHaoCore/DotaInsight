using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows.Threading;
using AutoUpdaterDotNET;
using DotaInsight.Helpers;
using Serilog;

namespace DotaInsight.Services;

/// <summary>手动检查更新的结果。</summary>
public enum UpdateCheckStatus
{
    /// <summary>已是最新版本。</summary>
    UpToDate,

    /// <summary>有可用新版本。</summary>
    UpdateAvailable,

    /// <summary>检查失败（网络不通 / 尚未发布 / 解析失败）。</summary>
    Failed
}

/// <param name="Status">检查结论。</param>
/// <param name="Release">解析出的版本信息，失败时为 null。</param>
/// <param name="Message">可直接展示给用户的说明。</param>
public sealed record UpdateCheckResult(UpdateCheckStatus Status, GitHubReleaseInfo? Release, string Message);

/// <summary>发起升级的结果。</summary>
public enum UpdateApplyStatus
{
    /// <summary>升级包已下载并交给 ZipExtractor，应用随后自动退出重启。</summary>
    Started,

    /// <summary>程序目录不可写，无法就地升级。</summary>
    NotWritable,

    /// <summary>下载环节失败或用户取消。</summary>
    Failed
}

/// <summary>
/// 应用内升级：以 GitHub Releases 为发布通道。
/// </summary>
public interface IUpdateService
{
    /// <summary>当前程序版本（发布时由 git tag 注入到程序集）。</summary>
    Version CurrentVersion { get; }

    /// <summary>当前版本展示串，如 v0.1.0。</summary>
    string CurrentVersionText { get; }

    /// <summary>主动检查更新（用户点击时用），不弹任何界面，只返回结论。</summary>
    Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default);

    /// <summary>下载并就地升级；返回 <see cref="UpdateApplyStatus.Started"/> 时应用会自行退出。</summary>
    UpdateApplyStatus Apply(GitHubReleaseInfo release);

    /// <summary>用系统浏览器打开发布页面（无法自动升级时的退路）。</summary>
    void OpenReleasePage(GitHubReleaseInfo release);

    /// <summary>启动后的静默检查：有新版才弹窗，拉取失败不打扰用户。</summary>
    void StartStartupCheck();
}

/// <summary>
/// 升级服务。分工：
/// <list type="bullet">
///   <item>发布通道 = GitHub Releases（最新版）/releases/latest，不需要额外托管 AppCast XML，
///         由 <see cref="GitHubReleaseParser"/> 把 JSON 翻译成 AutoUpdater 要的结构。</item>
///   <item>下载与安装复用 Autoupdater.NET：zip 包会被解压覆盖程序目录，然后自动重启。</item>
///   <item>启动检查走 <c>AutoUpdater.Start</c>（自带「稍后提醒 / 跳过此版本」按钮与状态持久化）；
///         手动检查走自己的 HttpClient —— 因为 AutoUpdater 在「上次点了稍后提醒」或「已有一次检查在跑」
///         时会直接 return 而不触发任何回调，无法给用户明确答复。</item>
/// </list>
/// </summary>
public sealed class UpdateService : IUpdateService
{
    /// <summary>本仓库的 releases/latest 接口（AutoUpdater 与手动检查共用）。</summary>
    public const string LatestReleaseApiUrl =
        "https://api.github.com/repos/ShenHaoCore/DotaInsight/releases/latest";

    /// <summary>发布页面，取不到具体版本的地址时兜底用。</summary>
    public const string LatestReleasePageUrl =
        "https://github.com/ShenHaoCore/DotaInsight/releases/latest";

    public const string HttpClientName = "github";

    /// <summary>
    /// GitHub API 客户端配置，注册（App）与验证工具共用一份：
    /// GitHub 对不带 User-Agent 的请求直接 403，Accept 决定返回的 JSON 版本。
    /// </summary>
    public static void ConfigureHttpClient(HttpClient client)
    {
        client.BaseAddress = new Uri("https://api.github.com/");
        client.Timeout = TimeSpan.FromSeconds(15);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DotaInsight/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    /// <summary>启动检查的延迟：避开窗口首帧与首页数据加载。</summary>
    private static readonly TimeSpan StartupCheckDelay = TimeSpan.FromSeconds(5);

    private const string UserAgent = "DotaInsight-Updater/1.0";

#if DEBUG
    /// <summary>Debug 构建（含离屏渲染 harness）不做启动检查，免得开发时被升级弹窗打断。</summary>
    private static readonly bool AutoCheckOnStartup = false;
#else
    private static readonly bool AutoCheckOnStartup = true;
#endif

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;

    public UpdateService(IHttpClientFactory httpClientFactory, ILogger logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger.ForContext<UpdateService>();
        CurrentVersion = ReadCurrentVersion();
        ConfigureAutoUpdater();
    }

    public Version CurrentVersion { get; }

    public string CurrentVersionText => $"v{CurrentVersion.ToString(3)}";

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var json = await _httpClientFactory
                .CreateClient(HttpClientName)
                .GetStringAsync(LatestReleaseApiUrl, cancellationToken)
                .ConfigureAwait(false);

            if (!GitHubReleaseParser.TryParse(json, out var release, out var error) || release is null)
            {
                return new UpdateCheckResult(UpdateCheckStatus.Failed, null, error);
            }

            if (release.Version <= CurrentVersion)
            {
                _logger.Information(
                    "检查更新：已是最新（当前 {Current}，远端 {Latest}）",
                    CurrentVersionText,
                    release.VersionText);
                return new UpdateCheckResult(
                    UpdateCheckStatus.UpToDate,
                    release,
                    $"当前已是最新版本（{CurrentVersionText}）。");
            }

            _logger.Information("检查更新：发现新版本 {Latest}（当前 {Current}）", release.VersionText, CurrentVersionText);
            return new UpdateCheckResult(
                UpdateCheckStatus.UpdateAvailable,
                release,
                $"发现新版本 {release.VersionText}（当前 {CurrentVersionText}）。");
        }
        catch (Exception ex) when (HttpCall.IsUserCancellation(ex, cancellationToken))
        {
            throw;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.Information("检查更新：仓库还没有发布过任何版本");
            return new UpdateCheckResult(UpdateCheckStatus.Failed, null, "该项目还没有发布过任何版本，暂无更新可用。");
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "检查更新失败");
            return new UpdateCheckResult(UpdateCheckStatus.Failed, null, $"无法连接更新服务：{ex.Message}");
        }
    }

    public UpdateApplyStatus Apply(GitHubReleaseInfo release)
    {
        if (!IsAppDirectoryWritable())
        {
            _logger.Warning("程序目录不可写，无法就地升级：{Directory}", AppContext.BaseDirectory);
            return UpdateApplyStatus.NotWritable;
        }

        AutoUpdater.SetOwner(System.Windows.Application.Current?.MainWindow);

        var args = new UpdateInfoEventArgs
        {
            CurrentVersion = release.Version.ToString(),
            DownloadURL = release.DownloadUrl,
            ChangelogURL = release.ChangelogUrl,
            Mandatory = new Mandatory { Value = false }
        };

        _logger.Information("开始下载升级包 {Version}：{Url}", release.VersionText, release.DownloadUrl);

        // 模态下载框，返回 true 时升级包已下载并交由 ZipExtractor 处理
        var started = AutoUpdater.DownloadUpdate(args);
        if (!started)
        {
            _logger.Warning("升级包下载未完成（用户取消或下载出错）");
            return UpdateApplyStatus.Failed;
        }

        _logger.Information("升级包已就绪，ZipExtractor 将覆盖程序目录并重启应用，现在退出");
        System.Windows.Application.Current?.Shutdown();
        return UpdateApplyStatus.Started;
    }

    public void OpenReleasePage(GitHubReleaseInfo release)
    {
        var url = string.IsNullOrWhiteSpace(release.ChangelogUrl) ? LatestReleasePageUrl : release.ChangelogUrl;

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "打开发布页面失败：{Url}", url);
        }
    }

    public void StartStartupCheck()
    {
        if (!AutoCheckOnStartup)
        {
            _logger.Debug("当前为 Debug 构建，跳过启动检查更新");
            return;
        }

        // 延后几秒，等窗口首帧与首页数据先落地
        var timer = new DispatcherTimer { Interval = StartupCheckDelay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _logger.Information("启动静默检查更新（当前 {Version}）", CurrentVersionText);
            AutoUpdater.SetOwner(System.Windows.Application.Current?.MainWindow);
            AutoUpdater.Start(LatestReleaseApiUrl);
        };
        timer.Start();
    }

    /// <summary>
    /// AutoUpdater 只认 AppCast XML，这里把 GitHub 的 JSON 现场翻译成它要的结构。
    /// 解析失败时留空 UpdateInfo —— 库会按「检查失败」处理，而 ReportErrors 默认为 false，不会弹窗。
    /// </summary>
    private void OnParseUpdateInfo(ParseUpdateInfoEventArgs args)
    {
        if (!GitHubReleaseParser.TryParse(args.RemoteData, out var release, out var error) || release is null)
        {
            _logger.Warning("启动检查：更新信息不可用（{Error}）", error);
            return;
        }

        _logger.Information(
            "启动检查：远端最新 {Latest}（当前 {Current}），安装包 {Package}",
            release.VersionText,
            CurrentVersionText,
            FileNameOf(release.DownloadUrl));

        args.UpdateInfo = new UpdateInfoEventArgs
        {
            CurrentVersion = release.Version.ToString(),
            DownloadURL = release.DownloadUrl,
            ChangelogURL = release.ChangelogUrl,
            Mandatory = new Mandatory { Value = false }
        };
    }

    private void ConfigureAutoUpdater()
    {
        // 显式给版本：单文件自包含发布下不依赖入口程序集探测
        AutoUpdater.InstalledVersion = CurrentVersion;
        AutoUpdater.AppTitle = "DotaInsight";

        // GitHub 接口对不带 User-Agent 的请求直接 403，必须设置
        AutoUpdater.HttpUserAgent = UserAgent;

        // 装在用户目录，就地覆盖不需要管理员权限（RunUpdateAsAdmin 默认 true 会弹 UAC）
        AutoUpdater.RunUpdateAsAdmin = false;

        // 「跳过此版本 / 稍后提醒」写入应用数据目录，不污染注册表
        Directory.CreateDirectory(AppPaths.Root);
        AutoUpdater.PersistenceProvider = new JsonFilePersistenceProvider(
            Path.Combine(AppPaths.Root, "update-state.json"));

        AutoUpdater.ParseUpdateInfoEvent += OnParseUpdateInfo;
    }

    private static Version ReadCurrentVersion()
        => typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0, 0);

    /// <summary>从下载地址里取文件名（地址异常时原样返回，日志而已，不值得抛）。</summary>
    private static string FileNameOf(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? Path.GetFileName(uri.LocalPath) : url;

    /// <summary>
    /// 程序目录能否被覆盖（ZipExtractor 要往这里写 exe）。
    /// 用一次性探测文件判断，避免装到 Program Files 这类只读目录时才在升级中途报错。
    /// </summary>
    private static bool IsAppDirectoryWritable()
    {
        try
        {
            var probe = Path.Combine(AppContext.BaseDirectory, $".update-probe-{Guid.NewGuid():N}.tmp");
            using (File.Create(probe, 1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
