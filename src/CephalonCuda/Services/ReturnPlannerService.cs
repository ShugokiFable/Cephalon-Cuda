using System.IO;
using CephalonCuda.Models;

namespace CephalonCuda.Services;

/// <summary>
/// Return-player coordinator. It derives only what can be supported by local state, EE.log timestamps,
/// tracked goals and live-data freshness. It never pretends to read the player's private account inventory.
/// </summary>
public sealed class ReturnPlannerService
{
    private readonly SettingsService _settings;
    private readonly Db _db;
    private readonly OnboardingService _onboarding;
    private readonly WorldStateService _worldState;
    private readonly DataIntelligenceService _intelligence;
    private readonly EeLogWatcher _eeLog;
    private readonly object _sessionLock = new();
    private long? _activeSessionId;

    public ReturnPlannerService(SettingsService settings, Db db, OnboardingService onboarding,
        WorldStateService worldState, DataIntelligenceService intelligence, EeLogWatcher eeLog)
    {
        _settings = settings;
        _db = db;
        _onboarding = onboarding;
        _worldState = worldState;
        _intelligence = intelligence;
        _eeLog = eeLog;
        CaptureExistingLogTimestamp();
        _settings.LastAppOpenedAt = DateTimeOffset.UtcNow;
        _eeLog.EventParsed += OnGameEvent;
    }

    public DateTimeOffset? DetectedLastPlayedAt
    {
        get
        {
            var latest = _settings.LastGameActivityAt;
            try
            {
                var path = _settings.EeLogPath;
                if (File.Exists(path))
                {
                    var logTime = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
                    if (latest is null || logTime > latest) latest = logTime;
                }
            }
            catch { }

            // When Warframe has just been opened after a long absence, LastGameActivityAt becomes
            // "now" immediately. Preserve the pre-return activity long enough to build the actual
            // return briefing, then switch back to the latest session after the briefing is accepted.
            var previous = _settings.PreviousGameActivityAt;
            if (latest is { } current && previous is { } prior &&
                current - prior >= TimeSpan.FromDays(7) &&
                DateTimeOffset.UtcNow - current <= TimeSpan.FromDays(1) &&
                (_settings.LastReturnBriefingAt is null || _settings.LastReturnBriefingAt < current))
                return prior;

            return latest;
        }
    }

    public bool ShouldAutoBrief
    {
        get
        {
            if (!_settings.AutoReturnBriefing) return false;
            var briefing = GetBriefing();
            if (!briefing.IsReturning) return false;
            return _settings.LastReturnBriefingAt is not { } at || DateTimeOffset.UtcNow - at > TimeSpan.FromDays(1);
        }
    }

    public void MarkBriefed() => _settings.LastReturnBriefingAt = DateTimeOffset.UtcNow;

    public void MarkPlayedNow(string? playerName = null)
    {
        RecordActivity(DateTimeOffset.UtcNow, playerName, "Manual", null);
        MarkBriefed();
    }

    public ReturnBriefing GetBriefing()
    {
        var lastPlayed = DetectedLastPlayedAt;
        var days = lastPlayed is null ? 0 : Math.Max(0, (int)Math.Floor((DateTimeOffset.UtcNow - lastPlayed.Value).TotalDays));
        var next = _onboarding.GetNextTasks(4);
        var ready = _db.Query("SELECT name FROM foundry_jobs WHERE claimed=0 AND ends_at<=@n ORDER BY ends_at LIMIT 4",
            r => r.GetString(0), ("@n", DateTimeOffset.UtcNow.ToString("O")));
        var goals = _onboarding.GetGoals().Take(3).ToList();
        var unclaimedCodes = (int)_db.Scalar<long>("SELECT COUNT(*) FROM redeem_codes WHERE claimed=0 AND status NOT IN ('expired','invalid')");
        var sources = _intelligence.GetSourceHealth();
        var stale = sources.Where(s => s.Status is "stale" or "error" or "unknown").ToList();

        var doNow = new List<string>();
        if (ready.Count > 0) doNow.Add($"Claim ready Foundry items: {string.Join(", ", ready)}.");
        if (next.Count > 0) doNow.Add($"Resume the next tracked objective: {next[0].Title}.");
        if (goals.Count > 0) doNow.Add($"Re-anchor on your highest-priority goal: {goals[0].Title}.");
        if (unclaimedCodes > 0) doNow.Add($"Review {unclaimedCodes} unclaimed redemption code{(unclaimedCodes == 1 ? "" : "s")}.");

        var world = _worldState.Current;
        if (world?.VoidTrader?.IsHere == true)
            doNow.Add($"Baro Ki'Teer is currently available at {world.VoidTrader.Location}; check the inventory before spending Ducats.");
        var alert = world?.Alerts.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a.Mission?.Reward?.AsString ?? a.Mission?.Reward?.ItemString));
        if (alert?.Mission is { } mission)
            doNow.Add($"Limited alert: {mission.Reward?.AsString ?? mission.Reward?.ItemString} at {mission.Node}.");
        var newsSinceReturn = GetOfficialNewsSince(lastPlayed);
        if (newsSinceReturn.Count > 0)
            doNow.Add($"Review {newsSinceReturn.Count} recent official update/news headline{(newsSinceReturn.Count == 1 ? "" : "s")} published since your last detected activity.");

        if (doNow.Count == 0) doNow.Add("Confirm your Mastery Rank, current quest and one concrete goal so the advisor can rebuild your route.");

        var checks = new List<string>
        {
            "Revalidate old builds before adding Forma, Catalysts, Reactors, Archon Shards or expensive Arcanes.",
            "Do not sell, subsume or convert unfamiliar legacy items until acquisition and crafting dependencies are checked.",
            "Compare old trade prices with live orders before buying or relisting anything.",
        };
        if (stale.Count > 0) checks.Insert(0, $"Refresh stale sources first: {string.Join(", ", stale.Select(s => s.DisplayName))}.");

        var fresh = sources.Select(s => $"{s.DisplayName}: {s.Status}, {s.ItemCount:N0} records" +
            (s.LastSuccessAt is { } at ? $", checked {FriendlyAge(at)}" : "")).ToList();

        string headline;
        string summary;
        if (lastPlayed is null)
        {
            headline = "BUILD YOUR ACCOUNT SNAPSHOT";
            summary = "No trustworthy last-played date was detected. The app will learn from EE.log while Warframe is running, or you can mark today manually.";
        }
        else if (days >= 180)
        {
            headline = $"RETURNING AFTER {days} DAYS";
            summary = "Treat this as a controlled re-entry: refresh data, inspect old investments, rebuild one dependable loadout, then resume progression without chasing every new system at once.";
        }
        else if (days >= 30)
        {
            headline = $"WELCOME BACK · {days} DAYS AWAY";
            summary = "Start with a delta check, one reliable loadout and a short goal stack. Ignore shiny side systems until their prerequisites or rewards serve your plan.";
        }
        else if (days >= 7)
        {
            headline = $"QUICK RETURN · {days} DAYS AWAY";
            summary = "Refresh live sources, claim anything ready, check time-limited activities and continue the highest-value tracked objective.";
        }
        else
        {
            headline = "CURRENT SESSION READY";
            summary = "Your tracked state is recent. Use the return plan as a compact session launcher rather than a full re-onboarding.";
        }

        return new ReturnBriefing
        {
            LastPlayedAt = lastPlayed,
            DaysAway = days,
            Headline = headline,
            Summary = summary,
            DoNow = doNow,
            CheckBeforeInvesting = checks,
            FreshData = fresh,
        };
    }

    public object BuildAdvisorSummary()
    {
        var b = GetBriefing();
        return new
        {
            b.LastPlayedAt,
            b.DaysAway,
            b.IsReturning,
            b.Headline,
            b.Summary,
            doNow = b.DoNow,
            checkBeforeInvesting = b.CheckBeforeInvesting,
            dataSources = b.FreshData,
            officialNewsSinceReturn = GetOfficialNewsSince(b.LastPlayedAt).Select(n => new
            {
                n.Message,
                n.Link,
                n.Date,
                n.Update,
                n.PrimeAccess,
            }),
            recentSessions = _db.Query("""
                SELECT started_at,ended_at,COALESCE(player_name,''),COALESCE(last_node,''),mission_count
                FROM player_sessions ORDER BY started_at DESC LIMIT 5
                """, r => new
            {
                startedAt = r.GetString(0),
                endedAt = r.IsDBNull(1) ? null : r.GetString(1),
                player = r.GetString(2),
                lastNode = r.GetString(3),
                missions = r.GetInt32(4),
            }),
        };
    }

    private void CaptureExistingLogTimestamp()
    {
        try
        {
            if (!File.Exists(_settings.EeLogPath)) return;
            var timestamp = new DateTimeOffset(File.GetLastWriteTimeUtc(_settings.EeLogPath), TimeSpan.Zero);
            if (_settings.LastGameActivityAt is null) _settings.LastGameActivityAt = timestamp;
        }
        catch { }
    }

    private void OnGameEvent(EeLogEvent ev)
    {
        RecordActivity(ev.Time.ToUniversalTime(), _eeLog.PlayerName, ev.Kind, _eeLog.CurrentNode);
    }

    private void RecordActivity(DateTimeOffset at, string? playerName, string kind, string? node)
    {
        var previous = _settings.LastGameActivityAt;
        if (previous is null || at - previous.Value > TimeSpan.FromMinutes(10))
            _settings.PreviousGameActivityAt = previous;
        _settings.LastGameActivityAt = at;

        lock (_sessionLock)
        {
            var startsNewSession = _activeSessionId is null || previous is null ||
                at - previous.Value > TimeSpan.FromMinutes(10);
            if (startsNewSession)
            {
                _db.Exec("UPDATE player_sessions SET ended_at=COALESCE(ended_at,@at) WHERE ended_at IS NULL",
                    ("@at", at.ToString("O")));
                _db.Exec("INSERT INTO player_sessions(started_at,player_name,first_node,last_node,source) VALUES(@at,@p,@n,@n,@s)",
                    ("@at", at.ToString("O")), ("@p", playerName), ("@n", node), ("@s", kind == "Manual" ? "manual" : "EE.log"));
                _activeSessionId = _db.Scalar<long>("SELECT id FROM player_sessions ORDER BY id DESC LIMIT 1");
            }
            else if (_activeSessionId is long activeSessionId)
            {
                _db.Exec("UPDATE player_sessions SET ended_at=@at,player_name=COALESCE(@p,player_name),last_node=COALESCE(@n,last_node),mission_count=mission_count+@m WHERE id=@id",
                    ("@at", at.ToString("O")), ("@p", playerName), ("@n", node),
                    ("@m", kind == "MissionStart" ? 1 : 0), ("@id", activeSessionId));
            }
        }
    }

    private List<NewsItem> GetOfficialNewsSince(DateTimeOffset? since)
    {
        var news = _worldState.Current?.News ?? [];
        if (since is null) return news.OrderByDescending(n => n.Date).Take(10).ToList();
        return news.Where(n => DateTimeOffset.TryParse(n.Date, out var published) && published >= since.Value)
            .OrderByDescending(n => n.Date)
            .Take(16)
            .ToList();
    }

    private static string FriendlyAge(DateTimeOffset at)
    {
        var age = DateTimeOffset.UtcNow - at;
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        if (age < TimeSpan.FromMinutes(2)) return "just now";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes}m ago";
        if (age < TimeSpan.FromDays(1)) return $"{(int)age.TotalHours}h ago";
        return $"{(int)age.TotalDays}d ago";
    }
}
