using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace CephalonCuda.Services;

public sealed class SettingsService(Db db)
{
    // Model roles per the routing plan: cheap/fast for background jobs, strong for chat, multimodal for vision.
    public const string DefaultChatModel = "deepseek/deepseek-v4-pro";
    public const string DefaultBackgroundModel = "deepseek/deepseek-v4-flash";
    public const string DefaultVisionModel = "minimax/minimax-m3";

    public static readonly string[] SuggestedModels =
    [
        "deepseek/deepseek-v4-pro",
        "deepseek/deepseek-v4-flash",
        "minimax/minimax-m3",
        "nvidia/nemotron-3-ultra-550b-a55b:free",
        "nvidia/nemotron-3-ultra-550b-a55b",
        "perceptron/perceptron-mk1",
    ];

    public string? Get(string key) =>
        db.Scalar<string>("SELECT value FROM settings WHERE key=@k", ("@k", key));

    public void Set(string key, string? value)
    {
        if (value is null)
            db.Exec("DELETE FROM settings WHERE key=@k", ("@k", key));
        else
            db.Exec("INSERT INTO settings(key,value) VALUES(@k,@v) ON CONFLICT(key) DO UPDATE SET value=@v",
                ("@k", key), ("@v", value));
    }

    public int GetInt(string key, int fallback = 0) =>
        int.TryParse(Get(key), out var v) ? v : fallback;

    public bool GetBool(string key, bool fallback = false) =>
        Get(key) is { } s ? s == "1" : fallback;

    public void SetBool(string key, bool value) => Set(key, value ? "1" : "0");

    // ---- appearance ---------------------------------------------------------

    /// <summary>Optional #RRGGBB accent override applied on top of the Verv theme.</summary>
    public string? CustomAccent
    {
        get => Get("custom_accent");
        set => Set("custom_accent", string.IsNullOrWhiteSpace(value) ? null : value.Trim());
    }

    /// <summary>UI zoom, 80–130 (%).</summary>
    public int UiScalePct
    {
        get => Math.Clamp(GetInt("ui_scale_pct", 100), 80, 130);
        set => Set("ui_scale_pct", Math.Clamp(value, 80, 130).ToString());
    }

    public string ThemeName
    {
        get => Get("theme_name") ?? "Verv";
        set => Set("theme_name", value);
    }

    public bool CompactMode
    {
        get => GetBool("compact_mode", false);
        set => SetBool("compact_mode", value);
    }

    public bool ReducedMotion
    {
        get => GetBool("reduced_motion", false);
        set => SetBool("reduced_motion", value);
    }

    public bool BeginnerWarnings
    {
        get => GetBool("beginner_warnings", true);
        set => SetBool("beginner_warnings", value);
    }

    public string AdvisorTone
    {
        get => Get("advisor_tone") ?? "Direct";
        set => Set("advisor_tone", value);
    }

    public string RoutineBudget
    {
        get => Get("routine_budget") ?? "60 minutes";
        set => Set("routine_budget", value);
    }

    public string? TennoCallsign
    {
        get => Get("tenno_callsign");
        set => Set("tenno_callsign", string.IsNullOrWhiteSpace(value) ? null : value.Trim());
    }

    /// <summary>Navigation tag opened after startup.</summary>
    public string StartupPage
    {
        get => Get("startup_page") ?? "path";
        set => Set("startup_page", value);
    }

    // ---- automation / return-player flow ------------------------------------

    /// <summary>Automatically refresh due live-data sources after startup.</summary>
    public bool AutoRefreshData
    {
        get => GetBool("auto_refresh_data", true);
        set => SetBool("auto_refresh_data", value);
    }

    /// <summary>Show a return briefing automatically after a meaningful absence.</summary>
    public bool AutoReturnBriefing
    {
        get => GetBool("auto_return_briefing", true);
        set => SetBool("auto_return_briefing", value);
    }

    /// <summary>Background refresh cadence, clamped to a respectful 15–120 minutes.</summary>
    public int DataRefreshMinutes
    {
        get => Math.Clamp(GetInt("data_refresh_minutes", 30), 15, 120);
        set => Set("data_refresh_minutes", Math.Clamp(value, 15, 120).ToString());
    }

    public DateTimeOffset? LastGameActivityAt
    {
        get => DateTimeOffset.TryParse(Get("last_game_activity_at"), out var v) ? v : null;
        set => Set("last_game_activity_at", value?.ToString("O"));
    }

    public DateTimeOffset? PreviousGameActivityAt
    {
        get => DateTimeOffset.TryParse(Get("previous_game_activity_at"), out var v) ? v : null;
        set => Set("previous_game_activity_at", value?.ToString("O"));
    }

    public DateTimeOffset? LastAppOpenedAt
    {
        get => DateTimeOffset.TryParse(Get("last_app_opened_at"), out var v) ? v : null;
        set => Set("last_app_opened_at", value?.ToString("O"));
    }

    public DateTimeOffset? LastReturnBriefingAt
    {
        get => DateTimeOffset.TryParse(Get("last_return_briefing_at"), out var v) ? v : null;
        set => Set("last_return_briefing_at", value?.ToString("O"));
    }

    public bool FirstRunCompleted
    {
        get => GetBool("first_run_completed", false);
        set => SetBool("first_run_completed", value);
    }

    // ---- immersive mode ------------------------------------------------------

    /// <summary>Right | Left | Float — where the app docks over the game in Immersive Mode.</summary>
    public string ImmersiveDock
    {
        get => Get("immersive_dock") ?? "Right";
        set => Set("immersive_dock", value);
    }

    /// <summary>Docked panel width as % of the game client width, 25–60.</summary>
    public int ImmersiveWidthPct
    {
        get => Math.Clamp(GetInt("immersive_width_pct", 38), 25, 60);
        set => Set("immersive_width_pct", Math.Clamp(value, 25, 60).ToString());
    }

    /// <summary>Window opacity in Immersive Mode, 70–100 (%). 100 skips layering entirely (safest for the Advisor webview).</summary>
    public int ImmersiveOpacityPct
    {
        get => Math.Clamp(GetInt("immersive_opacity_pct", 100), 70, 100);
        set => Set("immersive_opacity_pct", Math.Clamp(value, 70, 100).ToString());
    }

    public bool ImmersiveAutoEnter
    {
        get => GetBool("immersive_auto_enter", false);
        set => SetBool("immersive_auto_enter", value);
    }

    // ---- behavior ---------------------------------------------------------------

    public bool MinimizeToTray
    {
        get => GetBool("minimize_to_tray", false);
        set => SetBool("minimize_to_tray", value);
    }

    public bool OverlayOnStartup
    {
        get => GetBool("overlay_on_startup", false);
        set => SetBool("overlay_on_startup", value);
    }

    public bool FoundryNotifications
    {
        get => GetBool("foundry_notifications", true);
        set => SetBool("foundry_notifications", value);
    }

    // ---- typed settings ---------------------------------------------------

    public string ChatModel
    {
        get => Get("chat_model") ?? DefaultChatModel;
        set => Set("chat_model", value);
    }

    public string BackgroundModel
    {
        get => Get("background_model") ?? DefaultBackgroundModel;
        set => Set("background_model", value);
    }

    public string VisionModel
    {
        get => Get("vision_model") ?? DefaultVisionModel;
        set => Set("vision_model", value);
    }

    public int MasteryRank
    {
        get => GetInt("mastery_rank", 0);
        set => Set("mastery_rank", value.ToString());
    }

    public int PlatinumOwned
    {
        get => GetInt("platinum_owned", 0);
        set => Set("platinum_owned", value.ToString());
    }

    public bool OverlayEnabled
    {
        get => GetBool("overlay_enabled", true);
        set => SetBool("overlay_enabled", value);
    }

    public string EeLogPath
    {
        get => Get("eelog_path") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Warframe", "EE.log");
        set => Set("eelog_path", value);
    }

    // ---- secrets, DPAPI-encrypted at rest ---------------------------------

    private string? GetSecret(string key)
    {
        var blob = Get(key);
        if (string.IsNullOrEmpty(blob)) return null;
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(blob), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch { return null; }
    }

    private void SetSecret(string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) { Set(key, null); return; }
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(value.Trim()), null, DataProtectionScope.CurrentUser);
        Set(key, Convert.ToBase64String(bytes));
    }

    public string? ApiKey
    {
        get => GetSecret("openrouter_key");
        set => SetSecret("openrouter_key", value);
    }

    public bool HasApiKey => !string.IsNullOrEmpty(ApiKey);

    // ---- warframe.market account ------------------------------------------

    /// <summary>In-game name / profile slug used for public order lookups.</summary>
    public string? MarketUsername
    {
        get => Get("market_username");
        set => Set("market_username", string.IsNullOrWhiteSpace(value) ? null : value.Trim());
    }

    /// <summary>Access token copied from warframe.market DevTools (encrypted); enables authenticated /me calls.</summary>
    public string? MarketJwt
    {
        get => GetSecret("market_jwt");
        set => SetSecret("market_jwt", NormalizeMarketToken(value));
    }

    private static string? NormalizeMarketToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var token = value.Trim().Trim('"');
        if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) token = token[7..].Trim();
        else if (token.StartsWith("JWT ", StringComparison.OrdinalIgnoreCase)) token = token[4..].Trim();
        return token.Length == 0 ? null : token;
    }

    public bool HasMarketAccount => !string.IsNullOrWhiteSpace(MarketUsername) || !string.IsNullOrEmpty(MarketJwt);

    // ---- optional licensed community intelligence feed -------------------

    /// <summary>
    /// User-supplied HTTPS JSON feed containing the same schema accepted by the manual importer.
    /// This is intentionally not preconfigured to scrape any third-party site.
    /// </summary>
    public string? CommunityFeedUrl
    {
        get => Get("community_feed_url");
        set => Set("community_feed_url", string.IsNullOrWhiteSpace(value) ? null : value.Trim());
    }

    // ---- freeform profile notes injected into every AI call ---------------

    /// <summary>User-authored "everything about me" the advisor should know: owned frames, focus, goals, standings.</summary>
    public string? ProfileNotes
    {
        get => Get("profile_notes");
        set => Set("profile_notes", string.IsNullOrWhiteSpace(value) ? null : value.Trim());
    }
}
