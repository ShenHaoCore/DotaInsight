using DotaInsight.Helpers;
using DotaInsight.Models;
using Serilog;

namespace DotaInsight.Services;

/// <summary>
/// 已保存账户的 LiteDB 实现。
/// 数据放在 <see cref="CacheKeys.SavedAccounts"/> 条目里，该键被列为用户资产，
/// 清理缓存时会保留——否则用户攒下的账号卡片会被「清理缓存」一并抹掉。
/// </summary>
public sealed class AccountService : IAccountService
{
    /// <summary>保留上限，超出后淘汰最久未使用的账号。</summary>
    private const int MaxAccounts = 20;

    /// <summary>账号是长期数据，TTL 取得足够长等于不过期。</summary>
    private static readonly TimeSpan Retention = TimeSpan.FromDays(3650);

    private readonly ILiteDbCacheService _cache;
    private readonly ILogger _logger;
    private readonly object _sync = new();

    public AccountService(ILiteDbCacheService cache, ILogger logger)
    {
        _cache = cache;
        _logger = logger.ForContext<AccountService>();
    }

    public long? CurrentAccountId
    {
        get
        {
            lock (_sync)
            {
                return LoadBook().CurrentAccountId;
            }
        }
    }

    public IReadOnlyList<SavedAccount> GetAll()
    {
        lock (_sync)
        {
            var book = LoadBook();
            return book.Accounts
                .OrderByDescending(a => a.LastUsedUtc)
                .Select(a =>
                {
                    a.IsCurrent = a.AccountId == book.CurrentAccountId;
                    return a;
                })
                .ToList();
        }
    }

    public void SaveOrUpdate(PlayerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.AccountId <= 0)
        {
            return;
        }

        lock (_sync)
        {
            var book = LoadBook();
            var account = book.Accounts.FirstOrDefault(a => a.AccountId == profile.AccountId);
            if (account is null)
            {
                account = new SavedAccount { AccountId = profile.AccountId };
                book.Accounts.Add(account);
            }

            // 只在拿到有效值时才覆盖：某次查询降级到离线缓存时，
            // 不要把已经存好的昵称与头像抹成空。
            if (!string.IsNullOrWhiteSpace(profile.PersonaName))
            {
                account.PersonaName = profile.PersonaName;
            }

            if (!string.IsNullOrWhiteSpace(profile.AvatarUrl))
            {
                account.AvatarUrl = profile.AvatarUrl;
            }

            if (profile.RankTier is > 0)
            {
                account.RankTier = profile.RankTier;
            }

            if (profile.TotalMatches > 0)
            {
                account.Wins = profile.Wins;
                account.Losses = profile.Losses;
            }

            account.LastUsedUtc = DateTime.UtcNow;
            book.CurrentAccountId = profile.AccountId;

            TrimOverflow(book);
            Save(book);
        }
    }

    public void SetCurrent(long accountId)
    {
        lock (_sync)
        {
            var book = LoadBook();
            var account = book.Accounts.FirstOrDefault(a => a.AccountId == accountId);
            if (account is null)
            {
                return;
            }

            account.LastUsedUtc = DateTime.UtcNow;
            book.CurrentAccountId = accountId;
            Save(book);
        }
    }

    public bool Remove(long accountId)
    {
        lock (_sync)
        {
            var book = LoadBook();
            if (book.Accounts.RemoveAll(a => a.AccountId == accountId) == 0)
            {
                return false;
            }

            if (book.CurrentAccountId == accountId)
            {
                book.CurrentAccountId = null;
            }

            Save(book);
            _logger.Information("已移除保存的账号 AccountId={AccountId}", accountId);
            return true;
        }
    }

    public int MigrateLegacyIds(IEnumerable<string> legacyIds)
    {
        ArgumentNullException.ThrowIfNull(legacyIds);

        var added = 0;
        lock (_sync)
        {
            var book = LoadBook();
            // 旧记录没有时间戳，用迁移时刻按原顺序递减，保持「最近使用的在前」
            var stamp = DateTime.UtcNow;
            var index = 0;

            foreach (var raw in legacyIds)
            {
                index++;
                if (!SteamAccountId.TryParse(raw, out var accountId) || accountId <= 0)
                {
                    continue;
                }

                if (book.Accounts.Any(a => a.AccountId == accountId))
                {
                    continue;
                }

                book.Accounts.Add(new SavedAccount
                {
                    AccountId = accountId,
                    LastUsedUtc = stamp.AddSeconds(-index)
                });
                added++;
            }

            if (added > 0)
            {
                TrimOverflow(book);
                Save(book);
                _logger.Information("已并入旧版 SteamID 历史 {Count} 条", added);
            }
        }

        return added;
    }

    private static void TrimOverflow(AccountBook book)
    {
        while (book.Accounts.Count > MaxAccounts)
        {
            var oldest = book.Accounts.OrderBy(a => a.LastUsedUtc).First();
            book.Accounts.Remove(oldest);
        }
    }

    private AccountBook LoadBook()
    {
        try
        {
            return _cache.Get<AccountBook>(CacheKeys.SavedAccounts) ?? new AccountBook();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "读取已保存账户失败，按空列表处理");
            return new AccountBook();
        }
    }

    private void Save(AccountBook book)
    {
        try
        {
            _cache.Set(CacheKeys.SavedAccounts, book, Retention);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "保存账户列表失败");
        }
    }
}
