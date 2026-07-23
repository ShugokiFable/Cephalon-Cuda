namespace CephalonCuda.Models;

public sealed class RoadmapTask
{
    public string Id { get; set; } = "";
    public string Phase { get; set; } = "";
    public string Category { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string WhyItMatters { get; set; } = "";
    public string? ActionHint { get; set; }
    public string? SourceUrl { get; set; }
    public int SortOrder { get; set; }
    public int MinimumMr { get; set; }
    public bool Optional { get; set; }
    public bool Completed { get; set; }
    public bool Skipped { get; set; }
    public bool Pinned { get; set; }
    public string State => Completed ? "DONE" : Skipped ? "SKIPPED" : Pinned ? "PINNED" : "NEXT";
    public string Meta => $"{Phase} · {Category}" + (MinimumMr > 0 ? $" · Suggested MR {MinimumMr}+" : "") + (Optional ? " · Optional" : "");
}

public sealed class ShopVerdict
{
    public string RuleId { get; set; } = "";
    public string Verdict { get; set; } = "CHECK";
    public int Severity { get; set; }
    public string Title { get; set; } = "No direct rule matched";
    public string Reason { get; set; } = "Compare the Platinum price with the farm, blueprint, and player-market alternatives before buying.";
    public string BetterOption { get; set; } = "Ask the advisor for the exact item and your progression stage.";
    public string? ItemIntel { get; set; }
    public string VerdictDisplay => Verdict.ToUpperInvariant();
}

public sealed class RedeemCodeEntry
{
    public string Code { get; set; } = "";
    public string Title { get; set; } = "";
    public string Reward { get; set; } = "";
    public string Status { get; set; } = "unknown";
    public bool Claimed { get; set; }
    public string? ExpiresAt { get; set; }
    public string? SourceUrl { get; set; }
    public string State => Claimed ? "CLAIMED" : Status.ToUpperInvariant();
}

public sealed class PlayerGoal
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public string TargetName { get; set; } = "";
    public string Category { get; set; } = "General";
    public int Priority { get; set; } = 2;
    public string Status { get; set; } = "active";
    public string Notes { get; set; } = "";
    public string PriorityDisplay => Priority switch { 1 => "HIGH", 2 => "NORMAL", _ => "LOW" };
}

/// <summary>Deterministic return-player snapshot assembled before the AI adds recommendations.</summary>
public sealed class ReturnBriefing
{
    public DateTimeOffset? LastPlayedAt { get; set; }
    public int DaysAway { get; set; }
    public string Headline { get; set; } = "READY WHEN YOU ARE";
    public string Summary { get; set; } = "Confirm your account snapshot, then generate a focused return plan.";
    public List<string> DoNow { get; set; } = [];
    public List<string> CheckBeforeInvesting { get; set; } = [];
    public List<string> FreshData { get; set; } = [];
    public bool IsReturning => DaysAway >= 7;
    public string LastPlayedDisplay => LastPlayedAt is null ? "Not detected yet" : LastPlayedAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
}
