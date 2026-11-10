using DotaInsight.Models;
using DotaInsight.Services;
using Serilog;

namespace DotaInsight.Tests;

public class ScratchCacheCheck
{
    [Fact]
    public void Inspect_V6Entry()
    {
        using var cache = new LiteDbCacheService(new LoggerConfiguration().CreateLogger());
        var detail = cache.Get<MatchDetail>("opendota:matchDetail:v6:9070284784");
        Assert.Fail("RPT null=" + (detail is null) + " radiant=" + (detail?.Radiant.Count ?? -1)
            + " itemGold0=" + (detail?.Radiant.FirstOrDefault()?.ItemGold ?? -1)
            + " ids0=" + (detail?.Radiant.FirstOrDefault()?.ItemIds.Count ?? -1)
            + " spots=" + (detail?.Spots.Count ?? -1));
    }
}
