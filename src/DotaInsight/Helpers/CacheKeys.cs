namespace DotaInsight.Helpers;

/// <summary>
/// 缓存键集中定义，避免同一份数据在两处各写一个字符串而悄悄失配。
/// </summary>
public static class CacheKeys
{
    /// <summary>已保存账户（昵称 / 头像 / 段位）。</summary>
    public const string SavedAccounts = "saved_accounts";

    /// <summary>旧版战绩页查询历史（纯 ID 列表，仅用于一次性迁移到账户卡片）。</summary>
    public const string LegacyMatchHistoryIds = "match_history_ids";

    /// <summary>用户的主题选择（light / dark）。缺省 = 追随系统。</summary>
    public const string AppTheme = "app:theme";

    /// <summary>用户的网络设置（代理地址等）。缺省 = 直连。</summary>
    public const string NetworkSettings = "app:network_settings";

    /// <summary>Steam 自动导入的忽略名单（用户手动移除过的账号 ID，不再自动加回）。</summary>
    public const string SteamImport = "steam_import";

    /// <summary>
    /// 属于「用户资产」的键前缀：这些数据是用户攒下来的，清缓存时必须保留。
    /// 可再生的数据缓存（战绩、英雄资料等）不在此列。
    /// </summary>
    private static readonly string[] UserAssetPrefixes = [SavedAccounts, AppTheme, NetworkSettings, SteamImport];

    public static bool IsUserAsset(string key)
        => UserAssetPrefixes.Any(prefix => key.StartsWith(prefix, StringComparison.Ordinal));
}
