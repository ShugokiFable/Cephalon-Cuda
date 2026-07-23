namespace CephalonCuda.Models;

/// <summary>Row from the documented warframe.market /v2/items catalog (cached in SQLite).</summary>
public sealed class MarketItem
{
    public string Id { get; set; } = "";
    public string ItemName { get; set; } = "";
    public string UrlName { get; set; } = "";
    public string? Thumb { get; set; }
}

/// <summary>Bulk price enrichment from the legacy economics feed, with Ducat metadata seeded from /v2/items.</summary>
public sealed class PriceSnapshot
{
    public string ItemId { get; set; } = "";
    public string ItemName { get; set; } = "";
    public string UrlName { get; set; } = "";
    public int Ducats { get; set; }
    public double WaPrice { get; set; }   // volume-weighted average platinum
    public double Median { get; set; }
    public DateTimeOffset FetchedAt { get; set; }

    public double DucatsPerPlat => WaPrice > 0 ? Ducats / WaPrice : 0;
}

public sealed class MarketOrder
{
    public string OrderType { get; set; } = ""; // buy | sell
    public int Platinum { get; set; }
    public int Quantity { get; set; }
    public string UserName { get; set; } = "";
    public string UserStatus { get; set; } = ""; // ingame | online | offline
    public int? ModRank { get; set; }

    public string RankDisplay => ModRank is { } r ? r.ToString() : "—";
}

public sealed class PricePoint
{
    public DateTimeOffset Date { get; set; }
    public double Median { get; set; }
    public double AvgPrice { get; set; }
    public int Volume { get; set; }
}

public sealed class ItemDetails
{
    public string UrlName { get; set; } = "";
    public string ItemName { get; set; } = "";
    public int? Ducats { get; set; }
    public bool? Vaulted { get; set; }
    public int? MasteryLevel { get; set; }
    public string? WikiLink { get; set; }
    public int? TradingTax { get; set; }
    public int? MaxRank { get; set; }

    /// <summary>True when the item is a mod or arcane that can have multiple ranks.</summary>
    public bool IsRankable => MaxRank is > 0;
}

public sealed class RivenAuctionInfo
{
    public string WeaponUrlName { get; set; } = "";
    public string Name { get; set; } = "";       // riven suffix name
    public int? BuyoutPrice { get; set; }
    public int StartingPrice { get; set; }
    public int? TopBid { get; set; }
    public int ModRank { get; set; }
    public int Rerolls { get; set; }
    public int MasteryLevel { get; set; }
    public string Polarity { get; set; } = "";
    public string OwnerStatus { get; set; } = "";
    public List<RivenAttrInfo> Attributes { get; set; } = [];

    public string AttributesDisplay => string.Join(", ",
        Attributes.Select(a => $"{(a.Positive ? "+" : "")}{a.Value:0.#} {a.UrlName.Replace('_', ' ')}"));
}

public sealed class RivenAttrInfo
{
    public string UrlName { get; set; } = "";
    public double Value { get; set; }
    public bool Positive { get; set; }
}

/// <summary>One of the signed-in user's own listings, synced from warframe.market.</summary>
public sealed class UserMarketOrder
{
    public string OrderType { get; set; } = ""; // sell | buy
    public string ItemName { get; set; } = "";
    public string UrlName { get; set; } = "";
    public int Platinum { get; set; }
    public int Quantity { get; set; } = 1;
    public bool Visible { get; set; } = true;
    public int? Rank { get; set; }

    // Joined from the live market at sync time so the user sees if their price is stale.
    public double MarketLow { get; set; }
    public string Direction => OrderType.ToUpperInvariant();
    public string VisibleDisplay => Visible ? "" : "hidden";
    public string MarketLowDisplay => MarketLow > 0 ? $"{MarketLow:0}" : "—";
    public string RankDisplay => Rank is { } r ? r.ToString() : "—";
}

/// <summary>Authenticated identity from warframe.market /v2/me.</summary>
public sealed class MarketMe
{
    public string IngameName { get; set; } = "";
    public string Slug { get; set; } = "";
    public int Reputation { get; set; }
    public string? Platform { get; set; }
    public string? Locale { get; set; }
    public string? Role { get; set; }
    public int? Platinum { get; set; }   // wf.market wallet, if exposed
}

/// <summary>Manual platinum ledger entry (trading analytics).</summary>
public sealed class TradeEntry
{
    public long Id { get; set; }
    public DateTimeOffset Date { get; set; }
    public string ItemName { get; set; } = "";
    public int Quantity { get; set; } = 1;
    public int Platinum { get; set; }
    public bool IsSale { get; set; } = true; // true = plat in, false = plat out
    public string? Notes { get; set; }

    public int SignedPlat => IsSale ? Platinum : -Platinum;
    public string Direction => IsSale ? "SELL" : "BUY";
}
