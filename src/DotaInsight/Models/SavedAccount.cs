using System.Text.Json.Serialization;
using DotaInsight.Helpers;

namespace DotaInsight.Models;

/// <summary>
/// 已保存的 Steam 账户：把查询过的账号信息留在本地，做成卡片方便切换。
/// 只存展示所需的摘要（昵称 / 头像地址 / 段位 / 战绩），不含任何凭据或令牌。
/// </summary>
public sealed class SavedAccount
{
    /// <summary>OpenDota account_id（32 位）；输入 SteamID64 时已换算。</summary>
    public long AccountId { get; set; }

    public string PersonaName { get; set; } = string.Empty;

    public string AvatarUrl { get; set; } = string.Empty;

    public int? RankTier { get; set; }

    public int Wins { get; set; }

    public int Losses { get; set; }

    /// <summary>最近一次查询时间（UTC），用于排序与相对时间显示。</summary>
    public DateTime LastUsedUtc { get; set; }

    /// <summary>
    /// 是否当前选中，仅用于卡片高亮，不持久化——
    /// 当前账号由 <see cref="AccountBook.CurrentAccountId"/> 记录。
    /// </summary>
    [JsonIgnore]
    public bool IsCurrent { get; set; }

    /// <summary>
    /// 是否已拿到过账号资料。仅从旧版纯 ID 历史迁移来的账户为 false，
    /// 卡片上显示为待补全，查询一次即会写入昵称与头像。
    /// </summary>
    [JsonIgnore]
    public bool HasProfile => !string.IsNullOrWhiteSpace(AvatarUrl);

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(PersonaName) ? $"账号 {AccountId}" : PersonaName;

    [JsonIgnore]
    public string AccountIdText => AccountId.ToString();

    [JsonIgnore]
    public string RankText => RankTierFormatter.Format(RankTier);

    /// <summary>卡片第二行文字：已取得资料显示段位，否则提示待补全（「未定级」会误导）。</summary>
    [JsonIgnore]
    public string StatusText => HasProfile ? RankText : "资料待补全";

    [JsonIgnore]
    public string RecordText => Wins + Losses > 0 ? $"{Wins}胜 {Losses}负" : string.Empty;

    [JsonIgnore]
    public bool HasRecord => Wins + Losses > 0;

    /// <summary>相对时间：刚刚 / N 分钟前 / N 小时前 / 昨天 / N 天前 / MM-dd。</summary>
    [JsonIgnore]
    public string LastUsedText
    {
        get
        {
            if (LastUsedUtc == default)
            {
                return string.Empty;
            }

            var local = LastUsedUtc.ToLocalTime();
            var delta = DateTime.Now - local;
            if (delta <= TimeSpan.FromMinutes(1))
            {
                return "刚刚";
            }

            if (delta.TotalHours < 1)
            {
                return $"{(int)delta.TotalMinutes} 分钟前";
            }

            if (delta.TotalDays < 1)
            {
                return $"{(int)delta.TotalHours} 小时前";
            }

            return delta.TotalDays < 2 ? "昨天" : local.ToString("MM-dd");
        }
    }

    /// <summary>悬停提示：昵称 + 账号 ID + 段位/战绩。</summary>
    [JsonIgnore]
    public string ToolTipText
    {
        get
        {
            var lines = new List<string> { DisplayName, $"账号 ID {AccountIdText}" };
            lines.Add(HasRecord ? $"{RankText} · {RecordText}" : RankText);
            if (!HasProfile)
            {
                lines.Add("尚未获取资料，查询一次即可补全头像与昵称");
            }

            return string.Join('\n', lines);
        }
    }
}

/// <summary>
/// 已保存账户的集合与当前选中项，整体作为一个缓存条目持久化。
/// </summary>
public sealed class AccountBook
{
    public List<SavedAccount> Accounts { get; set; } = [];

    public long? CurrentAccountId { get; set; }
}
