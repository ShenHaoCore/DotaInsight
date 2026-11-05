namespace DotaInsight.Models;

/// <summary>
/// 分析结论等级：亮点 / 中性画像 / 待改进。
/// </summary>
public enum PlaystyleLevel
{
    /// <summary>做得好，保持。</summary>
    Good,

    /// <summary>信息性画像，无褒贬。</summary>
    Neutral,

    /// <summary>可改进，附具体建议。</summary>
    Improve
}

/// <summary>
/// 一条打法治理结论：现状 + 可执行建议。
/// </summary>
public sealed class PlaystyleFinding
{
    public PlaystyleLevel Level { get; init; }

    /// <summary>维度标题，如「场均死亡」。</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>现状数值描述，如「9.2 次/场」。</summary>
    public string Detail { get; init; } = string.Empty;

    /// <summary>可直接执行的建议文本。</summary>
    public string Suggestion { get; init; } = string.Empty;

    /// <summary>同级内排序权重，越大越靠前。</summary>
    public int Priority { get; init; }
}

/// <summary>
/// 打法样本：最近 N 场对位的聚合画像（每场均值 + 位置分布）。
/// 数据来自 OpenDota recentMatches（含 GPM/伤害/分路等列，契约见 odota/core
/// svc/api/playerFields.ts 的 recentMatchesCols）。record 以支持测试中 with 变体构造。
/// </summary>
public sealed record PlaystyleSample
{
    public int Games { get; init; }

    public int Wins { get; init; }

    public double AvgKills { get; init; }

    public double AvgDeaths { get; init; }

    public double AvgAssists { get; init; }

    public double AvgGpm { get; init; }

    public double AvgXpm { get; init; }

    public double AvgLastHits { get; init; }

    public double AvgHeroDamage { get; init; }

    public double AvgTowerDamage { get; init; }

    public double AvgHeroHealing { get; init; }

    public double AvgDurationMinutes { get; init; }

    /// <summary>各位置场次占比（0-1，已归类场次内归一）。</summary>
    public double SafeShare { get; init; }

    public double MidShare { get; init; }

    public double OffShare { get; init; }

    public double JungleShare { get; init; }

    public double RoamShare { get; init; }

    /// <summary>样本内不同英雄数。</summary>
    public int HeroPool { get; init; }

    /// <summary>组队对局（party_size &gt; 1）占比。</summary>
    public double PartyShare { get; init; }

    /// <summary>对局环境（lobby average_rank 均值），无数据为 null。</summary>
    public double? AverageLobbyRank { get; init; }

    /// <summary>是否主打核心位（三路核心占比 &gt;= 打野+游走占比）。</summary>
    public bool IsCore => SafeShare + MidShare + OffShare >= JungleShare + RoamShare;

    /// <summary>主打位置：占比最高的一路（游走/打野单列）。</summary>
    public (string Name, double Share) PrimaryLane
    {
        get
        {
            var lanes = new (string Name, double Share)[]
            {
                ("优势路", SafeShare),
                ("中路", MidShare),
                ("劣势路", OffShare),
                ("打野", JungleShare),
                ("游走", RoamShare)
            };
            return lanes.OrderByDescending(l => l.Share).First();
        }
    }

    public double WinRate => Games > 0 ? Wins * 100.0 / Games : 0;

    /// <summary>辅助位参战度：kills+assists 占比（位置感知规则的输入）。</summary>
    public double AssistShare => AvgKills + AvgAssists > 0 ? AvgAssists / (AvgKills + AvgAssists) : 0;
}

/// <summary>
/// 打法分析报告。
/// </summary>
public sealed class PlaystyleReport
{
    public long AccountId { get; init; }

    public PlaystyleSample? Sample { get; init; }

    public IReadOnlyList<PlaystyleFinding> Findings { get; init; } = Array.Empty<PlaystyleFinding>();

    /// <summary>一句话画像，可直接展示。</summary>
    public string SummaryText { get; init; } = string.Empty;

    /// <summary>待改进条数（供徽标/统计）。</summary>
    public int ImproveCount => Findings.Count(f => f.Level == PlaystyleLevel.Improve);

    /// <summary>亮点条数。</summary>
    public int GoodCount => Findings.Count(f => f.Level == PlaystyleLevel.Good);

    public bool IsEmpty => Sample is null || Sample.Games == 0;
}

/// <summary>
/// 打法规则引擎：把聚合样本翻译成「画像 + 可执行建议」。
/// 纯静态函数，不触网络与缓存——所有阈值都是有据可查的经验值（在建议文案中注明），
/// 位置感知是核心设计：给辅助位报「补刀少」、给核心位报「助攻少」都是误导。
/// </summary>
public static class PlaystyleAdvisor
{
    /// <summary>
    /// 从近期对位行聚合打法样本。分路判定：is_roaming 优先归游走，
    /// lane_role 1/2/3/4 = 优势/中/劣势/打野，未知的行不计入位置分布分母。
    /// </summary>
    public static PlaystyleSample? BuildSample(IReadOnlyList<RecentMatchItem> matches)
    {
        ArgumentNullException.ThrowIfNull(matches);
        var rows = matches.Where(m => m.MatchId > 0).ToList();
        if (rows.Count == 0)
        {
            return null;
        }

        var n = rows.Count;
        var laneCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var partyCount = 0;
        var lobbyRanks = new List<double>();

        foreach (var m in rows)
        {
            var lane = m.IsRoaming ? "游走" : m.LaneRole switch
            {
                1 => "优势路",
                2 => "中路",
                3 => "劣势路",
                4 => "打野",
                _ => null
            };
            if (lane is not null)
            {
                laneCounts[lane] = laneCounts.TryGetValue(lane, out var c) ? c + 1 : 1;
            }

            if (m.PartySize > 1)
            {
                partyCount++;
            }

            if (m.AverageRank > 0)
            {
                lobbyRanks.Add(m.AverageRank);
            }
        }

        var laneTotal = laneCounts.Values.Sum();
        double Share(string key) => laneTotal > 0 ? (laneCounts.TryGetValue(key, out var c) ? c : 0) / (double)laneTotal : 0;

        return new PlaystyleSample
        {
            Games = n,
            Wins = rows.Count(m => m.IsWin),
            AvgKills = rows.Average(m => m.Kills),
            AvgDeaths = rows.Average(m => m.Deaths),
            AvgAssists = rows.Average(m => m.Assists),
            AvgGpm = rows.Average(m => m.Gpm),
            AvgXpm = rows.Average(m => m.Xpm),
            AvgLastHits = rows.Average(m => m.LastHits),
            AvgHeroDamage = rows.Average(m => m.HeroDamage),
            AvgTowerDamage = rows.Average(m => m.TowerDamage),
            AvgHeroHealing = rows.Average(m => m.HeroHealing),
            AvgDurationMinutes = rows.Average(m => m.DurationSeconds) / 60.0,
            SafeShare = Share("优势路"),
            MidShare = Share("中路"),
            OffShare = Share("劣势路"),
            JungleShare = Share("打野"),
            RoamShare = Share("游走"),
            HeroPool = rows.Select(m => m.HeroId).Where(h => h > 0).Distinct().Count(),
            PartyShare = partyCount / (double)n,
            AverageLobbyRank = lobbyRanks.Count > 0 ? lobbyRanks.Average() : null
        };
    }

    public static PlaystyleReport Evaluate(long accountId, PlaystyleSample? sample)
    {
        if (sample is null || sample.Games == 0)
        {
            return new PlaystyleReport
            {
                AccountId = accountId,
                Sample = sample,
                SummaryText = "暂无对局数据"
            };
        }

        var findings = new List<PlaystyleFinding>();
        AddLaneProfile(findings, sample);
        AddDeathRule(findings, sample);
        AddWinRateRule(findings, sample);
        AddHeroPoolRule(findings, sample);

        if (sample.IsCore)
        {
            AddCoreRules(findings, sample);
        }
        else
        {
            AddSupportRules(findings, sample);
        }

        AddPartyRule(findings, sample);

        // 待改进最前、画像次之、亮点垫底；枚举声明序是 Good(0)<Neutral(1)<Improve(2)，
        // 直接 OrderBy(Level) 会把亮点排到最前 —— 用显式分级，不依赖声明顺序。
        var ordered = findings
            .OrderBy(f => f.Level switch
            {
                PlaystyleLevel.Improve => 0,
                PlaystyleLevel.Neutral => 1,
                _ => 2
            })
            .ThenByDescending(f => f.Priority)
            .ToList();

        var lane = sample.PrimaryLane;
        return new PlaystyleReport
        {
            AccountId = accountId,
            Sample = sample,
            Findings = ordered,
            SummaryText = $"基于最近 {sample.Games} 场对局 · 主打{lane.Name}（{lane.Share:P0}）· 英雄池 {sample.HeroPool} 个"
        };
    }

    private static void AddLaneProfile(List<PlaystyleFinding> findings, PlaystyleSample s)
    {
        var (name, share) = s.PrimaryLane;
        var advice = name switch
        {
            "优势路" => "一号位以对线补刀和野区轮转效率为根基，前 10 分钟的每一波兵都值得复盘。",
            "中路" => "中单是节奏发动机：控符时间、支援边路的时机决定整场走向。",
            "劣势路" => "三号位的首要目标是让对方核心难受，换血与拉野比补刀数更重要。",
            "打野" => "打野位注意线野平衡：完全脱离兵线会让三路同时承压。",
            _ => "游走位以节奏和视野为优先，避免与核心争夺线上资源。"
        };

        findings.Add(new PlaystyleFinding
        {
            Level = PlaystyleLevel.Neutral,
            Title = "位置画像",
            Detail = $"主打{name}，占比 {share:P0}（{s.Games} 场）",
            Suggestion = advice,
            Priority = 50
        });
    }

    private static void AddDeathRule(List<PlaystyleFinding> findings, PlaystyleSample s)
    {
        if (s.AvgDeaths >= 9)
        {
            findings.Add(new PlaystyleFinding
            {
                Level = PlaystyleLevel.Improve,
                Title = "死亡过于频繁",
                Detail = $"场均死亡 {s.AvgDeaths:F1} 次",
                Suggestion = "每局复盘死亡回放：多数死亡源于越塔过深与无视野推进。练习每 5 秒扫一次小地图，阵亡前先问「这一步收益是什么」。",
                Priority = 90
            });
        }
        else if (s.AvgDeaths >= 6.5)
        {
            findings.Add(new PlaystyleFinding
            {
                Level = PlaystyleLevel.Improve,
                Title = "死亡率偏高",
                Detail = $"场均死亡 {s.AvgDeaths:F1} 次",
                Suggestion = "给关键装备设置保命预案（BKB/薄葬/推推），推进与打野时留意对方减员信息。",
                Priority = 60
            });
        }
        else if (s.AvgDeaths > 0)
        {
            findings.Add(new PlaystyleFinding
            {
                Level = PlaystyleLevel.Good,
                Title = "生存数据健康",
                Detail = $"场均死亡 {s.AvgDeaths:F1} 次",
                Suggestion = "保持当前的风险判断：少死一次就是多一波打钱时间。",
                Priority = 20
            });
        }
    }

    private static void AddWinRateRule(List<PlaystyleFinding> findings, PlaystyleSample s)
    {
        if (s.WinRate >= 60)
        {
            findings.Add(new PlaystyleFinding
            {
                Level = PlaystyleLevel.Good,
                Title = "近期胜率出色",
                Detail = $"胜率 {s.WinRate:F0}%（{s.Wins} 胜 {s.Games - s.Wins} 负）",
                Suggestion = "当前的英雄选择与打法节奏有效，保持并沉淀为固定套路。",
                Priority = 70
            });
        }
        else if (s.WinRate <= 40)
        {
            findings.Add(new PlaystyleFinding
            {
                Level = PlaystyleLevel.Improve,
                Title = "近期胜率偏低",
                Detail = $"胜率 {s.WinRate:F0}%（{s.Wins} 胜 {s.Games - s.Wins} 负）",
                Suggestion = "从失败局找共性：是对线劣势、资源分配还是团战站位？只改一个变量再观察 5 局。",
                Priority = 55
            });
        }
    }

    private static void AddHeroPoolRule(List<PlaystyleFinding> findings, PlaystyleSample s)
    {
        if (s.HeroPool <= 2)
        {
            findings.Add(new PlaystyleFinding
            {
                Level = PlaystyleLevel.Neutral,
                Title = "英雄池高度集中",
                Detail = $"{s.Games} 场只用了 {s.HeroPool} 个英雄",
                Suggestion = "熟练度是优势，但要为被 ban / 被针对准备 1-2 个同位置备选。",
                Priority = 40
            });
        }
        else if (s.HeroPool >= 9)
        {
            findings.Add(new PlaystyleFinding
            {
                Level = PlaystyleLevel.Neutral,
                Title = "尝试的英雄较分散",
                Detail = $"{s.Games} 场用了 {s.HeroPool} 个英雄",
                Suggestion = "集中练习 2-3 个本命英雄比广泛尝试更容易形成肌肉记忆与上分节奏。",
                Priority = 30
            });
        }
    }

    private static void AddCoreRules(List<PlaystyleFinding> findings, PlaystyleSample s)
    {
        if (s.AvgGpm < 400)
        {
            findings.Add(new PlaystyleFinding
            {
                Level = PlaystyleLevel.Improve,
                Title = "刷钱效率偏低",
                Detail = $"场均 GPM {s.AvgGpm:F0}（核心位）",
                Suggestion = "对线期 10 分钟补刀定 50+ 的目标；重点回看 5-15 分钟的打钱空窗期，中期在「推进/打野/参战」间保持资源入账。",
                Priority = 80
            });
        }
        else if (s.AvgGpm >= 550)
        {
            findings.Add(new PlaystyleFinding
            {
                Level = PlaystyleLevel.Good,
                Title = "刷钱效率优秀",
                Detail = $"场均 GPM {s.AvgGpm:F0}",
                Suggestion = "经济领先时注意转化为推进与视野压制，别把优势停在装备栏里。",
                Priority = 40
            });
        }

        var damagePerMin = s.AvgDurationMinutes > 0 ? s.AvgHeroDamage / s.AvgDurationMinutes : 0;
        if (damagePerMin < 350)
        {
            findings.Add(new PlaystyleFinding
            {
                Level = PlaystyleLevel.Improve,
                Title = "输出占比偏低",
                Detail = $"英雄伤害 {s.AvgHeroDamage:F0}/场（约 {damagePerMin:F0}/分钟）",
                Suggestion = "核对出装顺序是否成型太慢，以及团战是否在正确时机入场；核心位的伤害曲线应随装备陡增。",
                Priority = 45
            });
        }

        if (s.AvgTowerDamage < 1000)
        {
            findings.Add(new PlaystyleFinding
            {
                Level = PlaystyleLevel.Neutral,
                Title = "推塔贡献有限",
                Detail = $"场均对塔伤害 {s.AvgTowerDamage:F0}",
                Suggestion = "经济优势要兑换成防御塔；带线施压与抱团推塔时随身带 TP。",
                Priority = 25
            });
        }
    }

    private static void AddSupportRules(List<PlaystyleFinding> findings, PlaystyleSample s)
    {
        if (s.AssistShare < 0.45)
        {
            findings.Add(new PlaystyleFinding
            {
                Level = PlaystyleLevel.Improve,
                Title = "参战度偏低",
                Detail = $"助攻占比 {s.AssistShare:P0}（场均 {s.AvgAssists:F1} 助攻 / {s.AvgKills:F1} 击杀）",
                Suggestion = "辅助的价值在参战：跟随团队节奏游走、及时 TP 支援，而不是在线上囤经验。",
                Priority = 65
            });
        }
        else if (s.AssistShare >= 0.7)
        {
            findings.Add(new PlaystyleFinding
            {
                Level = PlaystyleLevel.Good,
                Title = "团队参与度高",
                Detail = $"助攻占比 {s.AssistShare:P0}",
                Suggestion = "参战节奏很好，下一步把经济花在团队装（眼/雾/团队光环）上放大价值。",
                Priority = 35
            });
        }

        // 辅助位刻意不评 GPM/补刀：低经济是位置的正常代价，评了就是误导。
        if (s.AvgHeroHealing >= 3000)
        {
            findings.Add(new PlaystyleFinding
            {
                Level = PlaystyleLevel.Good,
                Title = "治疗贡献突出",
                Detail = $"场均治疗 {s.AvgHeroHealing:F0}",
                Suggestion = "保持治疗装与站位选择，团队续航是隐性胜率。",
                Priority = 30
            });
        }
    }

    private static void AddPartyRule(List<PlaystyleFinding> findings, PlaystyleSample s)
    {
        if (s.PartyShare >= 0.8)
        {
            findings.Add(new PlaystyleFinding
            {
                Level = PlaystyleLevel.Neutral,
                Title = "以组队对局为主",
                Detail = $"组队占比 {s.PartyShare:P0}",
                Suggestion = "开黑配合熟练，但单排更能锻炼独立决策；可穿插单排验证个人 Carry 能力。",
                Priority = 15
            });
        }
    }
}
