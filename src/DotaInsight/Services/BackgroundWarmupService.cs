using System.Diagnostics;
using Serilog;

namespace DotaInsight.Services;

/// <summary>
/// 启动后台预热：软件开启后延迟一段时间，按「一次一件、间隔拉开」的节奏逐个补缓存
/// （物品目录 → 职业选手榜单 → 已存账号的战绩，最近使用的账号优先）。
/// 全部走缓存语义——新鲜的零网络跳过，过期的补拉。
/// 用户主动操作（<see cref="INetworkGate"/>）期间让路，全程无 UI、无提示、不打扰。
/// </summary>
public interface IBackgroundWarmupService
{
    /// <summary>启动预热循环（幂等，重复调用无效）。</summary>
    void Start();
}

public sealed class BackgroundWarmupService : IBackgroundWarmupService
{
    /// <summary>启动延迟：让首屏加载稳下来再开始。</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(15);

    /// <summary>预热步骤间隔：一次一件，绝不并发轰炸线路。</summary>
    private static readonly TimeSpan StepGap = TimeSpan.FromSeconds(20);

    /// <summary>账号之间的间隔（账号可能逐个拉取，比步骤间隔更长）。</summary>
    private static readonly TimeSpan AccountGap = TimeSpan.FromSeconds(25);

    private static readonly TimeSpan BusyPollInterval = TimeSpan.FromSeconds(3);

    private readonly IAccountService _accountService;
    private readonly IMatchAnalysisService _matchAnalysis;
    private readonly IProSceneService _proScene;
    private readonly IItemCatalogService _itemCatalog;
    private readonly INetworkGate _gate;
    private readonly ILogger _logger;
    private int _started;

    public BackgroundWarmupService(
        IAccountService accountService,
        IMatchAnalysisService matchAnalysis,
        IProSceneService proScene,
        IItemCatalogService itemCatalog,
        INetworkGate gate,
        ILogger logger)
    {
        _accountService = accountService;
        _matchAnalysis = matchAnalysis;
        _proScene = proScene;
        _itemCatalog = itemCatalog;
        _gate = gate;
        _logger = logger.ForContext<BackgroundWarmupService>();
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        _ = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        try
        {
            await Task.Delay(StartupDelay).ConfigureAwait(false);
            _logger.Information("后台预热开始");

            await WarmStepAsync("物品目录", () => _itemCatalog.GetItemCostsAsync()).ConfigureAwait(false);
            await WarmStepAsync("职业选手榜单", () => _proScene.GetProSceneAsync()).ConfigureAwait(false);

            // 已存账号逐个预热：GetAll 按最近使用排序，最常用的最先补
            var accounts = _accountService.GetAll();
            foreach (var account in accounts)
            {
                await WaitForUserIdleAsync().ConfigureAwait(false);
                await WarmStepAsync(
                    $"账号 {account.AccountId}",
                    () => _matchAnalysis.WarmupAccountAsync(account.AccountId)).ConfigureAwait(false);
                await Task.Delay(AccountGap).ConfigureAwait(false);
            }

            _logger.Information("后台预热完成");
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "后台预热循环异常终止");
        }
    }

    /// <summary>用户操作进行中就让路（轮询等待，无超时——预热不着急）。</summary>
    private async Task WaitForUserIdleAsync()
    {
        while (_gate.IsUserBusy)
        {
            await Task.Delay(BusyPollInterval).ConfigureAwait(false);
        }
    }

    private async Task WarmStepAsync(string name, Func<Task> action)
    {
        await WaitForUserIdleAsync().ConfigureAwait(false);
        var sw = Stopwatch.StartNew();
        try
        {
            await action().ConfigureAwait(false);
            _logger.Debug("预热完成 {Name}（{Elapsed}ms）", name, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "预热失败，跳过 {Name}", name);
        }
        finally
        {
            await Task.Delay(StepGap).ConfigureAwait(false);
        }
    }
}
