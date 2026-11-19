using DotaInsight.Helpers;
using DotaInsight.Models;
using Serilog;

namespace DotaInsight.Services;

/// <summary>
/// 本地 Steam 账号自动导入：启动后静默读取本机 Steam 最近登录的账号，
/// 不在账号列表时补一张占位卡——昵称直接用 Steam 侧 PersonaName，
/// 头像段位等真实资料由后台预热按缓存语义补齐，导入本身保持纯本地、零网络。
/// 用户手动移除过的账号记入忽略名单，不再自动加回（手动点「从 Steam 导入」仍可找回）。
/// </summary>
public interface ISteamAccountImportService
{
    /// <summary>启动导入（幂等，重复调用无效）。</summary>
    void Start();

    /// <summary>记录被用户手动移除的账号，自动导入不再将其加回。</summary>
    void MarkDismissed(long accountId);
}

public sealed class SteamAccountImportService : ISteamAccountImportService
{
    /// <summary>忽略名单上限：防止长期累积无限膨胀，超出淘汰最旧的记录。</summary>
    private const int MaxDismissed = 64;

    /// <summary>忽略名单与账号列表同寿命：10 年 TTL 等于不过期。</summary>
    private static readonly TimeSpan Retention = TimeSpan.FromDays(3650);

    private readonly ISteamAccountDetector _detector;
    private readonly IAccountService _accountService;
    private readonly ILiteDbCacheService _cache;
    private readonly ILogger _logger;
    private readonly object _sync = new();
    private int _started;

    /// <summary>被移除的账号 ID，按时间正序存（末尾最新），淘汰从头部出。</summary>
    private List<long> _dismissed;

    public SteamAccountImportService(
        ISteamAccountDetector detector,
        IAccountService accountService,
        ILiteDbCacheService cache,
        ILogger logger)
    {
        _detector = detector;
        _accountService = accountService;
        _cache = cache;
        _logger = logger.ForContext<SteamAccountImportService>();
        _dismissed = LoadDismissed();
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        _ = Task.Run(RunOnce);
    }

    public void MarkDismissed(long accountId)
    {
        lock (_sync)
        {
            // 先移除再追加：重复移除同一账号时刷新到末尾，淘汰始终发生在最旧的头部
            _dismissed.Remove(accountId);
            _dismissed.Add(accountId);
            while (_dismissed.Count > MaxDismissed)
            {
                _dismissed.RemoveAt(0);
            }

            SaveDismissed();
        }
    }

    /// <summary>检测并导入一次；同步执行供测试直调，Start 里丢进线程池跑。</summary>
    internal void RunOnce()
    {
        try
        {
            var user = _detector.GetMostRecent();
            if (user is null)
            {
                _logger.Debug("未检测到本地 Steam 登录账号，跳过自动导入");
                return;
            }

            lock (_sync)
            {
                if (_dismissed.Contains(user.AccountId))
                {
                    _logger.Debug("账号 {AccountId} 被用户移除过，不自动导入", user.AccountId);
                    return;
                }
            }

            if (_accountService.GetAll().Any(a => a.AccountId == user.AccountId))
            {
                _logger.Debug("账号 {AccountId} 已在列表中，跳过自动导入", user.AccountId);
                return;
            }

            // 已有当前账号时不抢选中；全新账号簿（首启）才设为当前——
            // 这样用户首次进战绩页会按「默认加载当前账号」的逻辑自动首查
            var makeCurrent = _accountService.CurrentAccountId is null;
            _accountService.SaveOrUpdate(new PlayerProfile
            {
                AccountId = user.AccountId,
                PersonaName = user.PersonaName
            }, makeCurrent);

            _logger.Information(
                "已从本地 Steam 导入登录账号 AccountId={AccountId} PersonaName={PersonaName}",
                user.AccountId,
                user.PersonaName);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "自动导入本地 Steam 账号失败");
        }
    }

    private List<long> LoadDismissed()
    {
        try
        {
            return _cache.Get<List<long>>(CacheKeys.SteamImport) ?? [];
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "读取 Steam 导入忽略名单失败，按空名单处理");
            return [];
        }
    }

    private void SaveDismissed()
    {
        try
        {
            _cache.Set(CacheKeys.SteamImport, _dismissed, Retention);
        }
        catch (Exception ex)
        {
            // 持久化失败只影响下次启动多导入一次，不影响本次行为
            _logger.Warning(ex, "保存 Steam 导入忽略名单失败");
        }
    }
}
