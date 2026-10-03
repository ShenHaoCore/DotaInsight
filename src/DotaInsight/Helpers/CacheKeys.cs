namespace DotaInsight.Helpers;

/// <summary>
/// 缓存键集中定义，避免同一份数据在两处各写一个字符串而悄悄失配。
/// </summary>
public static class CacheKeys
{
    /// <summary>已保存账户（昵称 / 头像 / 段位）。</summary>
    public const string SavedAccounts = "saved_accounts";

    /// <summary>
    /// 属于「用户资产」的键前缀：这些数据是用户攒下来的，清缓存时必须保留。
    /// 可再生的数据缓存（战绩、英雄资料等）不在此列。
    /// </summary>
    private static readonly string[] UserAssetPrefixes = [SavedAccounts];

    public static bool IsUserAsset(string key)
        => UserAssetPrefixes.Any(prefix => key.StartsWith(prefix, StringComparison.Ordinal));
}
