using DotaInsight.Helpers;
using DotaInsight.Models;
using DotaInsight.Services;
using Serilog;

namespace DotaInsight.Tests;

/// <summary>
/// 本地 Steam 账号识别与自动导入：VDF 解析 / 检测降级 / 导入与忽略名单规则。
/// 检测器与导入服务都通过委托注入 / 内存缓存测试，不碰注册表、磁盘和真实 cache.db。
/// </summary>
public class SteamAccountImportTests
{
    private static readonly ILogger SilentLogger = new LoggerConfiguration().CreateLogger();

    /// <summary>loginusers.vdf 形状的样例：两个账号，MostRecent 的反而是旧时间戳。</summary>
    private const string SampleVdf = """
        "users"
        {
            "76561198000000001"
            {
                "AccountName"		"user_a"
                "PersonaName"		"玩家甲"
                "RememberPassword"		"1"
                "MostRecent"		"1"
                "Timestamp"		"1700000000"
            }
            "76561198000000002"
            {
                "AccountName"		"user_b"
                "PersonaName"		"玩家\"乙\""
                "RememberPassword"		"1"
                "MostRecent"		"0"
                "Timestamp"		"1790000000"
            }
        }
        """;

    /// <summary>没有 MostRecent 标记的样例：应回退到 Timestamp 最大的账号。</summary>
    private const string NoMostRecentVdf = """
        "users"
        {
            "76561198000000001"
            {
                "AccountName"		"user_a"
                "PersonaName"		"玩家甲"
                "Timestamp"		"1700000000"
            }
            "76561198000000002"
            {
                "AccountName"		"user_b"
                "PersonaName"		"玩家乙"
                "Timestamp"		"1790000000"
            }
        }
        """;

    /// <summary>
    /// 新版 Steam 客户端样例（2024+ 实机形态）：没有 MostRecent 字段，
    /// 时间戳键为小写 timestamp，最近登录改用 AutoLogin=1 标记。
    /// </summary>
    private const string NewClientVdf = """
        "users"
        {
            "76561198370291704"
            {
                "AccountName"		"minichen1992"
                "PersonaName"		"恩赐解脱"
                "RememberPassword"		"1"
                "AutoLogin"		"1"
                "timestamp"		"1795006493"
            }
            "76561198000000001"
            {
                "AccountName"		"user_a"
                "PersonaName"		"玩家甲"
                "timestamp"		"1800000000"
            }
        }
        """;

    private sealed class InMemoryCache : ILiteDbCacheService
    {
        private readonly Dictionary<string, string> _store = new(StringComparer.Ordinal);

        public T? Get<T>(string key)
            => _store.TryGetValue(key, out var json)
                ? System.Text.Json.JsonSerializer.Deserialize<T>(json)
                : default;

        public T? GetStale<T>(string key) => Get<T>(key);

        public void Set<T>(string key, T value, TimeSpan? ttl = null)
        {
            ArgumentNullException.ThrowIfNull(value);
            _store[key] = System.Text.Json.JsonSerializer.Serialize(value);
        }

        public int ClearAll()
        {
            var count = _store.Count;
            _store.Clear();
            return count;
        }
    }

    private sealed class FakeDetector : ISteamAccountDetector
    {
        private readonly SteamLocalUser? _user;

        public FakeDetector(SteamLocalUser? user)
        {
            _user = user;
        }

        public SteamLocalUser? GetMostRecent() => _user;
    }

    private static SteamLocalUser CreateSteamUser(long accountId)
        => new()
        {
            SteamId64 = SteamAccountId.SteamId64Base + accountId,
            AccountName = "account_" + accountId,
            PersonaName = "Steam昵称" + accountId,
            MostRecent = true,
            Timestamp = 1
        };

    private static SteamAccountImportService CreateImportService(
        InMemoryCache cache,
        ISteamAccountDetector? detector,
        out AccountService accountService)
    {
        accountService = new AccountService(cache, SilentLogger);
        return new SteamAccountImportService(detector ?? new FakeDetector(null), accountService, cache, SilentLogger);
    }

    // ---- VdfParser ----

    [Fact]
    public void Parse_ParsesUsersAndDecodesEscapes()
    {
        var root = VdfParser.Parse(SampleVdf);

        Assert.True(root.TryGetValue("users", out var users));
        Assert.Equal(2, users!.Count);

        var userA = users["76561198000000001"];
        Assert.Equal("玩家甲", userA["PersonaName"].Value);
        Assert.Equal("1", userA["MostRecent"].Value);

        // 引号转义被解码：PersonaName 里允许出现引号字符
        Assert.Equal("玩家\"乙\"", users["76561198000000002"]["PersonaName"].Value);
    }

    [Fact]
    public void Parse_DecodesBackslashEscape()
    {
        const string vdf = """
            "users"
            {
                "76561198000000001"
                {
                    "PersonaName"		"斜杠\\名字"
                }
            }
            """;

        var root = VdfParser.Parse(vdf);

        Assert.Equal("斜杠\\名字", root["users"]["76561198000000001"]["PersonaName"].Value);
    }

    [Fact]
    public void Parse_ReturnsEmptyForMalformedInput()
    {
        // 空文本与残缺结构都不抛异常；允许残留空小节（如「"users" {」未闭合），
        // 但绝不能捞到任何叶子值——调用方按「没有数据」降级
        foreach (var text in new[] { "", "   ", "\"users\" {", "users", "}}}{{\"" })
        {
            var root = VdfParser.Parse(text);
            Assert.DoesNotContain(root.Values, node => node.IsLeaf || node.Count > 0);
        }
    }

    // ---- SteamAccountDetector ----

    [Fact]
    public void GetMostRecent_PrefersMostRecentOverNewerTimestamp()
    {
        // 手动登录过 user_a 之后，即便 user_b 的记录时间更新，也应返回 user_a
        var detector = new SteamAccountDetector(
            () => @"C:\Steam",
            path => path == @"C:\Steam\config\loginusers.vdf" ? SampleVdf : null,
            SilentLogger);

        var user = detector.GetMostRecent();

        Assert.NotNull(user);
        Assert.Equal(76561198000000001L, user.SteamId64);
        Assert.Equal("玩家甲", user.PersonaName);
        Assert.Equal(39734273, user.AccountId);
    }

    [Fact]
    public void GetMostRecent_FallsBackToLargestTimestampWithoutFlag()
    {
        var detector = new SteamAccountDetector(
            () => @"C:\Steam",
            path => path == @"C:\Steam\config\loginusers.vdf" ? NoMostRecentVdf : null,
            SilentLogger);

        var user = detector.GetMostRecent();

        Assert.NotNull(user);
        Assert.Equal("玩家乙", user.PersonaName);
    }

    [Fact]
    public void GetMostRecent_SupportsNewClientAutoLoginField()
    {
        // 新版客户端没有 MostRecent：AutoLogin=1 优先于更新的 timestamp
        var detector = new SteamAccountDetector(
            () => @"C:\Steam",
            path => path == @"C:\Steam\config\loginusers.vdf" ? NewClientVdf : null,
            SilentLogger);

        var user = detector.GetMostRecent();

        Assert.NotNull(user);
        Assert.Equal("恩赐解脱", user.PersonaName);
        Assert.Equal(410025976, user.AccountId);
    }

    [Fact]
    public void GetMostRecent_ReturnsNullWhenSteamMissing()
    {
        var detector = new SteamAccountDetector(() => null, _ => null, SilentLogger);

        Assert.Null(detector.GetMostRecent());
    }

    [Fact]
    public void GetMostRecent_FallsBackToDefaultPathWhenRegistryMissing()
    {
        // 注册表读不到时按常见默认安装目录继续找
        var detector = new SteamAccountDetector(
            () => null,
            path => path.EndsWith(@"config\loginusers.vdf", StringComparison.Ordinal) ? SampleVdf : null,
            SilentLogger);

        var user = detector.GetMostRecent();

        Assert.NotNull(user);
        Assert.Equal("玩家甲", user.PersonaName);
    }

    // ---- SteamAccountImportService ----

    [Fact]
    public void RunOnce_FirstRunSetsSteamAccountCurrent()
    {
        // 全新账号簿：导入即设为当前，进战绩页会自动首查这个账号
        var cache = new InMemoryCache();
        var service = CreateImportService(cache, new FakeDetector(CreateSteamUser(39734273)), out var accounts);

        service.RunOnce();

        var account = Assert.Single(accounts.GetAll());
        Assert.Equal(39734273, account.AccountId);
        Assert.Equal("Steam昵称39734273", account.PersonaName);
        Assert.True(account.IsCurrent);
        Assert.Equal(39734273, accounts.CurrentAccountId);
    }

    [Fact]
    public void RunOnce_DoesNotStealCurrentAccount()
    {
        // 已有账号簿：静默补卡不改「当前选中」
        var cache = new InMemoryCache();
        var service = CreateImportService(cache, new FakeDetector(CreateSteamUser(39734273)), out var accounts);
        accounts.SaveOrUpdate(new PlayerProfile { AccountId = 1111506, PersonaName = "已有账号" });

        service.RunOnce();

        Assert.Equal(2, accounts.GetAll().Count);
        Assert.Equal(1111506, accounts.CurrentAccountId);
        var imported = accounts.GetAll().Single(a => a.AccountId == 39734273);
        Assert.Equal("Steam昵称39734273", imported.PersonaName);
        Assert.False(imported.IsCurrent);
    }

    [Fact]
    public void RunOnce_SkipsAlreadySavedAccount()
    {
        var cache = new InMemoryCache();
        var service = CreateImportService(cache, new FakeDetector(CreateSteamUser(39734273)), out var accounts);
        accounts.SaveOrUpdate(new PlayerProfile { AccountId = 39734273, PersonaName = "OpenDota昵称" });

        service.RunOnce();

        var account = Assert.Single(accounts.GetAll());
        // 已保存的账号不该被 Steam 侧昵称覆盖成占位信息
        Assert.Equal("OpenDota昵称", account.PersonaName);
    }

    [Fact]
    public void RunOnce_SkipsDismissedAccount()
    {
        var cache = new InMemoryCache();
        var service = CreateImportService(cache, new FakeDetector(CreateSteamUser(39734273)), out var accounts);

        service.MarkDismissed(39734273);
        service.RunOnce();

        Assert.Empty(accounts.GetAll());
        Assert.Null(accounts.CurrentAccountId);
    }

    [Fact]
    public void RunOnce_DoesNothingWithoutSteam()
    {
        var cache = new InMemoryCache();
        var service = CreateImportService(cache, new FakeDetector(null), out var accounts);

        service.RunOnce();

        Assert.Empty(accounts.GetAll());
    }

    [Fact]
    public void MarkDismissed_PersistsAcrossRestart()
    {
        // 模拟重启：新实例从同一缓存读回忽略名单，被移除的账号不复活
        var cache = new InMemoryCache();
        var service = CreateImportService(cache, new FakeDetector(null), out _);
        service.MarkDismissed(39734273);

        var restarted = CreateImportService(cache, new FakeDetector(CreateSteamUser(39734273)), out var accounts);
        restarted.RunOnce();

        Assert.Empty(accounts.GetAll());
    }

    [Fact]
    public void MarkDismissed_TrimsOldestBeyondCap()
    {
        var cache = new InMemoryCache();
        var service = CreateImportService(cache, new FakeDetector(null), out _);

        for (var i = 100; i < 165; i++)
        {
            service.MarkDismissed(i);
        }

        // 最早标记的 100 被淘汰：重启后该 ID 允许重新自动导入
        var restarted = CreateImportService(cache, new FakeDetector(CreateSteamUser(100)), out var accounts);
        restarted.RunOnce();

        var account = Assert.Single(accounts.GetAll());
        Assert.Equal(100, account.AccountId);
    }
}
