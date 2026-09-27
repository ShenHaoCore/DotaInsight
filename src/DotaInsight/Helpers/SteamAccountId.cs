namespace DotaInsight.Helpers;

/// <summary>
/// Steam / OpenDota 账号 ID 解析。
/// </summary>
public static class SteamAccountId
{
    public const long SteamId64Base = 76561197960265728L;

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

        accountId = value > SteamId64Base ? value - SteamId64Base : value;
        return accountId > 0;
    }
}
