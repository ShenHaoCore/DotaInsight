namespace DotaInsight.Helpers;

/// <summary>
/// Steam / OpenDota 账号 ID 解析。
/// </summary>
public static class SteamAccountId
{
    public const long SteamId64Base = 76561197960265728L;

    /// <summary>
    /// OpenDota account_id 的有效上限（SteamID32 域为 uint32）。
    /// 超出该域的输入既不是 32 位账号，也不是合法 SteamID64 的换算结果（多为漏抄/多抄一位），应直接拒绝。
    /// </summary>
    public const long MaxAccountId = 4_294_967_295L;

    public static bool TryParse(string? input, out long accountId)
    {
        accountId = 0;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var text = input.Trim();
        if (!long.TryParse(text, out var value) || value <= 0)
        {
            return false;
        }

        // SteamID64 → account_id；value == base 会换算出 0，属于无效账号，
        // 不能被当成「账号 id = base 本身」放行（旧逻辑会放行这个 41 位的无效 ID）。
        var candidate = value > SteamId64Base ? value - SteamId64Base : value;
        if (candidate is <= 0 or > MaxAccountId)
        {
            return false;
        }

        accountId = candidate;
        return true;
    }
}
