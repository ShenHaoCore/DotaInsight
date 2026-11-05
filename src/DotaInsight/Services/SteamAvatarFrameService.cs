using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using DotaInsight.Helpers;
using Serilog;

namespace DotaInsight.Services;

/// <summary>
/// Steam 头像框（点数商店装扮）查询。
/// </summary>
public interface ISteamAvatarFrameService
{
    /// <summary>
    /// 获取玩家当前装备的头像框图片地址。
    /// 无框返回 null（会缓存「确认无框」）；网络失败返回 null（不缓存失败结果）。
    /// </summary>
    Task<string?> GetFrameUrlAsync(long accountId, CancellationToken cancellationToken = default);
}

/// <summary>
/// 头像框数据来自 steamcommunity 个人资料页（免 key），框图在 economy CDN。
/// 两者在部分地区被墙：所有失败一律静默降级为无框，绝不向上抛——
/// 头像框是锦上添花，不能让它拖垮或污染战绩查询。
/// </summary>
public sealed class SteamAvatarFrameService : ISteamAvatarFrameService
{
    public const string HttpClientName = "SteamAvatarFrame";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILiteDbCacheService _cache;
    private readonly ILogger _logger;

    /// <summary>网络失败后的进程内冷却：期间不再发起请求。被墙时连接虽是瞬时失败，
    /// 也不值得每次战绩查询都白打一遍。</summary>
    private static readonly ConcurrentDictionary<long, DateTime> FailureCooldown = new();

    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(10);

    /// <summary>框是长期装扮，确认有框缓存两周；无框/私有可能被用户改变（买框、改隐私），
    /// 只缓存 24 小时。</summary>
    private static readonly TimeSpan FrameTtl = TimeSpan.FromDays(14);
    private static readonly TimeSpan NoFrameTtl = TimeSpan.FromHours(24);

    public SteamAvatarFrameService(IHttpClientFactory httpClientFactory, ILiteDbCacheService cache, ILogger logger)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _logger = logger.ForContext<SteamAvatarFrameService>();
    }

    /// <summary>v2：解析器演进时换版本号，避免旧键的错误结果（如确认无框）长期压住新解析。</summary>
    private static string CacheKey(long accountId) => $"steam:frame:v3:{accountId}";

    /// <summary>资料页响应分类（纯函数，可测）。</summary>
    internal enum SteamProfileHtmlKind
    {
        /// <summary>公开资料页：含头像区（profile_avatar）。</summary>
        Profile,

        /// <summary>完全私有页：只有 profile_private_info 占位，无头像区。</summary>
        PrivateProfile,

        /// <summary>错误页 / 限流页 / 其他非资料页。</summary>
        NotProfile
    }

    internal static SteamProfileHtmlKind ClassifyProfileHtml(string html)
    {
        // 私有页判定放前面：私有页也可能保留部分 avatar 相关字符串的变体
        if (html.Contains("profile_private_info", StringComparison.Ordinal))
        {
            return SteamProfileHtmlKind.PrivateProfile;
        }

        return html.Contains("profile_avatar", StringComparison.Ordinal)
            ? SteamProfileHtmlKind.Profile
            : SteamProfileHtmlKind.NotProfile;
    }

    public async Task<string?> GetFrameUrlAsync(long accountId, CancellationToken cancellationToken = default)
    {
        if (accountId <= 0)
        {
            return null;
        }

        // 空字符串 = 上次已确认无框；非空 = 已知框地址（图片本体由 HeroImageCache 落盘）
        var cached = _cache.Get<string>(CacheKey(accountId));
        if (cached is not null)
        {
            return cached.Length > 0 ? cached : null;
        }

        if (FailureCooldown.TryGetValue(accountId, out var coolUntil) && DateTime.UtcNow < coolUntil)
        {
            return null;
        }

        try
        {
            var steamId64 = SteamAccountId.SteamId64Base + accountId;
            var client = _httpClientFactory.CreateClient(HttpClientName);
            var html = await client
                .GetStringAsync($"https://steamcommunity.com/profiles/{steamId64}", cancellationToken)
                .ConfigureAwait(false);

            // 分类响应：公开资料页 / 完全私有页 / 不是资料页（错误页、限流页）
            var kind = ClassifyProfileHtml(html);
            if (kind == SteamProfileHtmlKind.PrivateProfile)
            {
                // 私有页不渲染头像区，等同「不可查」——按确认无框缓存，
                // 避免每次查询都对它白打三次重试
                _cache.Set(CacheKey(accountId), string.Empty, NoFrameTtl);
                FailureCooldown.TryRemove(accountId, out _);
                _logger.Information("头像框不可查：该账号的 Steam 资料页为私有 AccountId={AccountId}", accountId);
                return null;
            }

            if (kind != SteamProfileHtmlKind.Profile)
            {
                var title = System.Text.RegularExpressions.Regex.Match(
                    html, "<title>\\s*(.*?)\\s*</title>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                throw new InvalidOperationException(
                    $"返回内容不是资料页 len={html.Length} title={(title.Success ? title.Groups[1].Value : "(无 title)")}");
            }

            var frameUrl = SteamAvatarFrameParser.ExtractFrameUrl(html);
            _cache.Set(CacheKey(accountId), frameUrl ?? string.Empty, frameUrl is null ? NoFrameTtl : FrameTtl);
            FailureCooldown.TryRemove(accountId, out _);
            _logger.Information("头像框已解析 AccountId={AccountId} HasFrame={HasFrame}", accountId, frameUrl is not null);
            return frameUrl;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 用户取消：不属于网络失败，原样上抛由调用方决定
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            // TaskCanceledException 走到这里只剩超时
            FailureCooldown[accountId] = DateTime.UtcNow.Add(Cooldown);
            _logger.Warning(ex, "头像框获取失败，静默降级为无框 AccountId={AccountId}", accountId);

            // 过期缓存兜底：之前拿到过框地址就先用着（框图多半还在图片缓存里）
            return _cache.GetStale<string>(CacheKey(accountId)) is { Length: > 0 } stale ? stale : null;
        }
    }
}
