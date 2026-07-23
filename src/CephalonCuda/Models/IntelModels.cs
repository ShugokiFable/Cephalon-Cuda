namespace CephalonCuda.Models;

public sealed class SourceHealth
{
    public string SourceKey { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Status { get; set; } = "unknown";
    public int ItemCount { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public DateTimeOffset? NextRefreshAt { get; set; }
    public string? Error { get; set; }
    public string? SourceUrl { get; set; }

    public string CountDisplay => ItemCount.ToString("N0");
    public string LastSyncDisplay => LastSuccessAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "never";
    public string LastSyncLabel => $"Last sync: {LastSyncDisplay}";
    public string StateDisplay => Status switch
    {
        "ok" => "LIVE / CACHED",
        "refreshing" => "REFRESHING",
        "error" => "STALE / ERROR",
        "stale" => "STALE CACHE",
        "manual" => "MANUAL IMPORT",
        "local" => "LOCAL / INDEXED",
        _ => Status.ToUpperInvariant(),
    };
}

public sealed class KnowledgeHit
{
    public string Source { get; set; } = "";
    public string ExternalId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Tags { get; set; } = "";
    public string? SourceUrl { get; set; }
    public string? Freshness { get; set; }

    public string SourceDisplay => Source switch
    {
        "wfcd" => "WFCD",
        "market" => "WF.MARKET",
        "official-drop" => "OFFICIAL DROPS",
        "guide" => "LOCAL GUIDE",
        "community-tier" => "COMMUNITY TIER",
        "community-build" => "COMMUNITY BUILD",
        _ => Source.ToUpperInvariant(),
    };
}

public sealed class CommunityImportResult
{
    public string SourceName { get; set; } = "Community import";
    public int TierCount { get; set; }
    public int BuildCount { get; set; }
    public DateTimeOffset ImportedAt { get; set; } = DateTimeOffset.Now;
    public string? SourceUpdatedAt { get; set; }
    public int TotalCount => TierCount + BuildCount;
}

public sealed class MarketLiveMetric
{
    public string UrlName { get; set; } = "";
    public string ItemName { get; set; } = "";
    public double LowestSell { get; set; }
    public double LowestIngameSell { get; set; }
    public double HighestBuy { get; set; }
    public int SellCount { get; set; }
    public int BuyCount { get; set; }
    public DateTimeOffset FetchedAt { get; set; }
    public double Spread => LowestSell > 0 && HighestBuy > 0 ? LowestSell - HighestBuy : 0;
}

public sealed class OfficialDropRecord
{
    public string Id { get; set; } = "";
    public string ItemName { get; set; } = "";
    public string SourceType { get; set; } = "";
    public string Location { get; set; } = "";
    public string Rotation { get; set; } = "";
    public double? Chance { get; set; }
    public string Rarity { get; set; } = "";
    public string Detail { get; set; } = "";
}
