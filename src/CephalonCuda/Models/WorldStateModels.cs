namespace CephalonCuda.Models;

// DTOs for https://api.warframestat.us/pc payload (camelCase JSON, case-insensitive deserialization).

public sealed class WorldState
{
    public string? Timestamp { get; set; }
    public List<Fissure> Fissures { get; set; } = [];
    public Sortie? Sortie { get; set; }
    public ArchonHunt? ArchonHunt { get; set; }
    public List<Invasion> Invasions { get; set; } = [];
    public VoidTrader? VoidTrader { get; set; }
    public List<Alert> Alerts { get; set; } = [];
    public WorldCycle? CetusCycle { get; set; }
    public WorldCycle? VallisCycle { get; set; }
    public WorldCycle? CambionCycle { get; set; }
    public WorldCycle? DuviriCycle { get; set; }
    public WorldCycle? EarthCycle { get; set; }
    public List<NewsItem> News { get; set; } = [];
    public Arbitration? Arbitration { get; set; }
    public Nightwave? Nightwave { get; set; }

    public DateTimeOffset FetchedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Fissure
{
    public string? Id { get; set; }
    public string? Node { get; set; }
    public string? MissionType { get; set; }
    public string? Enemy { get; set; }
    public string? Tier { get; set; }
    public int TierNum { get; set; }
    public string? Expiry { get; set; }
    public string? Eta { get; set; }
    public bool IsStorm { get; set; }
    public bool IsHard { get; set; }
    public bool Expired { get; set; }
}

public sealed class Sortie
{
    public string? Boss { get; set; }
    public string? Faction { get; set; }
    public string? Eta { get; set; }
    public string? Activation { get; set; }
    public string? Expiry { get; set; }
    public List<SortieVariant> Variants { get; set; } = [];

    /// <summary>Live countdown from the expiry timestamp — the api's eta string is empty on the /pc payload.</summary>
    public string TimeLeft => Services.TimeUtil.Countdown(Expiry);
}

public sealed class SortieVariant
{
    public string? MissionType { get; set; }
    public string? Node { get; set; }
    public string? Modifier { get; set; }
}

public sealed class ArchonHunt
{
    public string? Boss { get; set; }
    public string? Faction { get; set; }
    public string? Eta { get; set; }
    public string? Activation { get; set; }
    public string? Expiry { get; set; }
    public List<ArchonMission> Missions { get; set; } = [];

    public string TimeLeft => Services.TimeUtil.Countdown(Expiry);
}

public sealed class ArchonMission
{
    public string? Node { get; set; }
    public string? Type { get; set; }
}

public sealed class Invasion
{
    public string? Node { get; set; }
    public string? Desc { get; set; }
    public InvasionSide? Attacker { get; set; }
    public InvasionSide? Defender { get; set; }
    public double Completion { get; set; }
    public bool Completed { get; set; }
    public bool VsInfestation { get; set; }
    public string? Eta { get; set; }
}

public sealed class InvasionSide
{
    public string? Faction { get; set; }
    public Reward? Reward { get; set; }
}

public sealed class Reward
{
    public string? AsString { get; set; }
    public string? ItemString { get; set; }
    public long Credits { get; set; }
}

public sealed class VoidTrader
{
    public string? Character { get; set; }
    public string? Location { get; set; }
    public bool Active { get; set; }
    public string? StartString { get; set; }
    public string? EndString { get; set; }
    public string? Activation { get; set; }
    public string? Expiry { get; set; }
    public List<BaroItem> Inventory { get; set; } = [];

    // The /pc payload leaves active/startString/endString empty — derive from the timestamps instead.
    public DateTimeOffset? ArrivesAt => Services.TimeUtil.Parse(Activation);
    public DateTimeOffset? LeavesAt => Services.TimeUtil.Parse(Expiry);
    public bool IsHere => ArrivesAt is { } a && LeavesAt is { } l &&
                          DateTimeOffset.UtcNow >= a && DateTimeOffset.UtcNow < l;

    /// <summary>"Here at X — leaves in 1d 4h" or "Arrives at X in 6d 2h".</summary>
    public string StatusLine => IsHere
        ? $"{Character} is HERE at {Location} — leaves in {Services.TimeUtil.Countdown(LeavesAt)}"
        : $"{Character} arrives at {Location} in {Services.TimeUtil.Countdown(ArrivesAt)}";
}

public sealed class BaroItem
{
    public string? Item { get; set; }
    public int Ducats { get; set; }
    public long Credits { get; set; }
}

public sealed class Alert
{
    public AlertMission? Mission { get; set; }
    public string? Eta { get; set; }
}

public sealed class AlertMission
{
    public string? Node { get; set; }
    public string? Type { get; set; }
    public Reward? Reward { get; set; }
}

public sealed class WorldCycle
{
    public string? State { get; set; }
    public string? TimeLeft { get; set; }
    public bool IsDay { get; set; }
    public bool IsWarm { get; set; }
}

public sealed class NewsItem
{
    public string? Message { get; set; }
    public string? Link { get; set; }
    public string? Date { get; set; }
    public bool Update { get; set; }
    public bool PrimeAccess { get; set; }
    public bool Stream { get; set; }
}

public sealed class Arbitration
{
    public string? Node { get; set; }
    public string? Type { get; set; }
    public string? Enemy { get; set; }
}

public sealed class Nightwave
{
    public string? Tag { get; set; }
    public int Season { get; set; }
    public List<NightwaveChallenge> ActiveChallenges { get; set; } = [];
}

public sealed class NightwaveChallenge
{
    public string? Title { get; set; }
    public string? Desc { get; set; }
    public int Reputation { get; set; }
    public bool IsDaily { get; set; }
    public bool IsElite { get; set; }
}
