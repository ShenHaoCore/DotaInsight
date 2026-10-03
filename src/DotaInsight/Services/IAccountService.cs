using DotaInsight.Models;

namespace DotaInsight.Services;

/// <summary>
/// 已保存账户的读写入口：查询过的账号会被记住并做成卡片，方便在账号间切换。
/// </summary>
public interface IAccountService
{
    /// <summary>按最近使用倒序返回全部账户，并标记当前选中项。</summary>
    IReadOnlyList<SavedAccount> GetAll();

    /// <summary>当前选中的账号 Id；从未选过为 null。</summary>
    long? CurrentAccountId { get; }

    /// <summary>查询成功后写入或更新账户信息，并把该账号设为当前。</summary>
    void SaveOrUpdate(PlayerProfile profile);

    /// <summary>仅把某账号设为当前（不改变已存信息）。</summary>
    void SetCurrent(long accountId);

    /// <summary>删除账户；删的是当前账号时当前项一并清空。</summary>
    bool Remove(long accountId);

    /// <summary>
    /// 把旧版纯 ID 历史记录并入账户列表。
    /// 幂等：已存在的账户不会被覆盖，只更新最近使用时间。
    /// 返回新增数量。
    /// </summary>
    int MigrateLegacyIds(IEnumerable<string> legacyIds);
}
