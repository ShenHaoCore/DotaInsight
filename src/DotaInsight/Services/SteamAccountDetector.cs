using System.IO;
using Microsoft.Win32;
using Serilog;

namespace DotaInsight.Services;

/// <summary>本机 Steam 客户端记住的一个登录账号。</summary>
public sealed class SteamLocalUser
{
    public required long SteamId64 { get; init; }

    public required string AccountName { get; init; }

    public required string PersonaName { get; init; }

    /// <summary>是否为最近登录本机 Steam 的账号（loginusers.vdf 的 MostRecent）。</summary>
    public bool MostRecent { get; init; }

    /// <summary>最近一次登录的 Unix 时间戳（秒）；旧客户端可能缺省为 0。</summary>
    public long Timestamp { get; init; }

    /// <summary>OpenDota 的 32 位 account_id。构造入口已保证 SteamId64 大于基值，可直接换算。</summary>
    public long AccountId => SteamId64 - Helpers.SteamAccountId.SteamId64Base;
}

/// <summary>
/// 读取本机 Steam 客户端记住的登录账号。
/// 注册表 <c>HKCU\Software\Valve\Steam\SteamPath</c> 定位安装目录，缺失时回退常见默认路径；
/// 账号清单在 <c>config/loginusers.vdf</c>（键为 SteamID64，含昵称与最近登录标记）。
/// 纯本地文件读取，零网络；Steam 未安装 / 未记住登录一律按「查不到」降级。
/// </summary>
public interface ISteamAccountDetector
{
    /// <summary>返回最近登录本机 Steam 的账号；检测不到返回 null。</summary>
    SteamLocalUser? GetMostRecent();
}

public sealed class SteamAccountDetector : ISteamAccountDetector
{
    /// <summary>注册表缺失时的回退安装目录（覆盖绝大多数默认安装）。</summary>
    private static readonly string[] FallbackSteamPaths =
    [
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) + @"\Steam",
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) + @"\Steam"
    ];

    private const string LoginUsersRelativePath = @"config\loginusers.vdf";

    private readonly Func<string?> _resolveSteamPath;
    private readonly Func<string, string?> _readAllText;
    private readonly ILogger _logger;

    public SteamAccountDetector(ILogger logger)
        : this(ReadSteamPathFromRegistry, SafeReadAllText, logger)
    {
    }

    /// <summary>测试缝：注入路径解析与文件读取委托，单元测试不碰注册表和真实磁盘。</summary>
    internal SteamAccountDetector(Func<string?> resolveSteamPath, Func<string, string?> readAllText, ILogger logger)
    {
        _resolveSteamPath = resolveSteamPath;
        _readAllText = readAllText;
        _logger = logger.ForContext<SteamAccountDetector>();
    }

    public SteamLocalUser? GetMostRecent()
    {
        try
        {
            return LoadUsers().FirstOrDefault();
        }
        catch (Exception ex)
        {
            // Steam 缺席是常态不是错误，Debug 一条即可，不打扰用户
            _logger.Debug(ex, "读取本地 Steam 登录账号失败");
            return null;
        }
    }

    /// <summary>按「最近登录优先」排序的全部记住账号；无则空列表。</summary>
    private List<SteamLocalUser> LoadUsers()
    {
        foreach (var steamPath in EnumerateSteamRoots())
        {
            var text = _readAllText(Path.Combine(steamPath, LoginUsersRelativePath));
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            var users = ParseUsers(text);
            if (users.Count > 0)
            {
                _logger.Debug("读到 {Count} 个 Steam 记住的登录账号", users.Count);
                return users;
            }
        }

        return [];
    }

    /// <summary>注册表路径优先，其后是常见默认路径；候选已归一化分隔符。</summary>
    private IEnumerable<string> EnumerateSteamRoots()
    {
        var fromRegistry = _resolveSteamPath();
        if (!string.IsNullOrWhiteSpace(fromRegistry))
        {
            // 注册表里的值可能用正斜杠（如 c:/program files (x86)/steam），统一成系统分隔符
            yield return fromRegistry.Trim().Replace('/', Path.DirectorySeparatorChar);
        }

        foreach (var fallback in FallbackSteamPaths)
        {
            yield return fallback;
        }
    }

    /// <summary>
    /// 解析 loginusers.vdf：键为 SteamID64，块内是 AccountName / PersonaName / MostRecent / Timestamp 等。
    /// 排序规则：MostRecent=1 优先，其余按 Timestamp 降序——手动导入与自动导入都取第一个。
    /// </summary>
    private List<SteamLocalUser> ParseUsers(string text)
    {
        var users = new List<SteamLocalUser>();
        var root = Helpers.VdfParser.Parse(text);
        if (!root.TryGetValue("users", out var usersNode) || usersNode.IsLeaf)
        {
            return users;
        }

        foreach (var (steamId64Text, userNode) in usersNode)
        {
            // 键必须是大于基值的 SteamID64；残留的 32 位 ID / 脏键直接跳过
            if (userNode.IsLeaf
                || !long.TryParse(steamId64Text, out var steamId64)
                || steamId64 <= Helpers.SteamAccountId.SteamId64Base)
            {
                continue;
            }

            userNode.TryGetValue("AccountName", out var accountNode);
            userNode.TryGetValue("PersonaName", out var personaNode);
            // 旧客户端用 MostRecent 标记最近登录；新版去掉该字段，改用 AutoLogin=1
            var mostRecent = (userNode.TryGetValue("MostRecent", out var recentNode)
                              || userNode.TryGetValue("AutoLogin", out recentNode))
                && recentNode.Value == "1";
            var timestamp = userNode.TryGetValue("Timestamp", out var tsNode)
                && long.TryParse(tsNode.Value, out var ts)
                ? ts
                : 0;

            users.Add(new SteamLocalUser
            {
                SteamId64 = steamId64,
                AccountName = accountNode?.Value ?? string.Empty,
                PersonaName = personaNode?.Value ?? string.Empty,
                MostRecent = mostRecent,
                Timestamp = timestamp
            });
        }

        return users
            .OrderByDescending(u => u.MostRecent ? 1 : 0)
            .ThenByDescending(u => u.Timestamp)
            .ToList();
    }

    private static string? ReadSteamPathFromRegistry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            return key?.GetValue("SteamPath") as string;
        }
        catch
        {
            return null;
        }
    }

    private static string? SafeReadAllText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch
        {
            return null;
        }
    }
}
