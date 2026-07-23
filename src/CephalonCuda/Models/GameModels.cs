namespace CephalonCuda.Models;

public sealed class InventoryItem
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public int Quantity { get; set; } = 1;
    public string? Notes { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    // Display-only, joined from the price snapshot at load time.
    public double UnitPlat { get; set; }
    public int UnitDucats { get; set; }
    public double TotalPlat => UnitPlat * Quantity;
    public int TotalDucats => UnitDucats * Quantity;
}

public sealed class FoundryJob
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public DateTimeOffset EndsAt { get; set; }
    public int DurationMinutes { get; set; }
    public bool Claimed { get; set; }

    public TimeSpan Remaining => EndsAt - DateTimeOffset.Now;
    public bool IsReady => Remaining <= TimeSpan.Zero;
    public string RemainingDisplay => IsReady
        ? "READY"
        : $"{(int)Remaining.TotalHours:00}:{Remaining.Minutes:00}:{Remaining.Seconds:00}";
}

public sealed class MasteryItem
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";      // Warframe | Weapon | Archwing | Companion...
    public string? Type { get; set; }           // Rifle, Pistol, ...
    public int MasteryReq { get; set; }
    public bool Mastered { get; set; }

    /// <summary>Mastery affinity granted when fully leveled: 6000 for frames/companions/archwings, 3000 for weapons.</summary>
    public int MasteryValue => Kind == "Weapon" ? 3000 : 6000;
}

/// <summary>One relic (per refinement state) from drops.warframestat.us relic data.</summary>
public sealed class RelicInfo
{
    public string Tier { get; set; } = "";      // Lith / Meso / Neo / Axi / Requiem
    public string Name { get; set; } = "";      // e.g. "A1"
    public string State { get; set; } = "";     // Intact / Exceptional / Flawless / Radiant
    public List<RelicReward> Rewards { get; set; } = [];

    public string DisplayName => $"{Tier} {Name}";
    public double ExpectedPlat { get; set; }    // computed EV from the price snapshot
    public double BestPlat { get; set; }
    public string? BestItem { get; set; }
}

public sealed class RelicReward
{
    public string ItemName { get; set; } = "";
    public string Rarity { get; set; } = "";
    public double Chance { get; set; }
    public double Plat { get; set; }            // joined
    public int Ducats { get; set; }             // joined
}

/// <summary>Result of OCRing one reward card on the relic reward-choice screen.</summary>
public sealed class RewardHit
{
    public string RawText { get; set; } = "";
    public string MatchedName { get; set; } = "";
    public double Score { get; set; }
    public double Plat { get; set; }
    public int Ducats { get; set; }
    public bool IsBest { get; set; }

    public string PlatDisplay => Plat > 0 ? $"{Plat:0}p" : "—";
    public string DucatDisplay => Ducats > 0 ? $"{Ducats}d" : "";
}

public sealed class ChatMsg
{
    public string Role { get; set; } = "user";  // system | user | assistant
    public string Content { get; set; } = "";
    public string? ImageBase64Png { get; set; } // when set, sent as multimodal content
}

public sealed class EeLogEvent
{
    public DateTimeOffset Time { get; set; } = DateTimeOffset.Now;
    public string Kind { get; set; } = "";      // Login, MissionStart, MissionEnd, RelicScreen, Raw...
    public string Detail { get; set; } = "";

    public string TimeDisplay => Time.ToLocalTime().ToString("HH:mm:ss");
}

/// <summary>User-entered riven for evaluation.</summary>
public sealed class RivenInput
{
    public string WeaponName { get; set; } = "";
    public string Stats { get; set; } = "";     // freeform lines: "+180% Damage" etc.
    public int Rerolls { get; set; }
    public int ModRank { get; set; } = 8;
    public string Polarity { get; set; } = "madurai";
}
