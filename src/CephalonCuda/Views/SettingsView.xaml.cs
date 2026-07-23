using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using CephalonCuda.Services;

namespace CephalonCuda.Views;

public partial class SettingsView : UserControl
{
    private bool _suppressModelEvents;
    private static readonly IReadOnlyDictionary<string, string> StartupPages = new Dictionary<string, string>
    {
        ["Tenno Path"] = "path",
        ["Live Overview"] = "dashboard",
        ["AI Advisor"] = "advisor",
        ["Market & Trading"] = "market",
        ["Inventory"] = "inventory",
        ["Relic Planner"] = "relics",
        ["Foundry"] = "foundry",
        ["Live Data & Intel"] = "intel",
    };

    public SettingsView()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadCurrent();
    }

    private void LoadCurrent()
    {
        var s = App.Services.Settings;
        _suppressModelEvents = true;
        FillModelBox(ChatModelBox, s.ChatModel);
        FillModelBox(BackgroundModelBox, s.BackgroundModel);
        FillModelBox(VisionModelBox, s.VisionModel);

        // Appearance + immersive + behavior (same suppression guard: setting values fires change events).
        ThemeGallery.ItemsSource = ThemeService.All;
        ThemeGallery.SelectedItem = ThemeService.Find(s.ThemeName);
        StartupPageBox.ItemsSource = StartupPages.Keys.ToList();
        StartupPageBox.SelectedItem = StartupPages.FirstOrDefault(x => x.Value == s.StartupPage).Key ?? "Tenno Path";
        CompactModeBox.IsChecked = s.CompactMode;
        ReducedMotionBox.IsChecked = s.ReducedMotion;
        AdvisorToneBox.ItemsSource = new[] { "Direct", "Coach", "Minimal", "Deep-dive" };
        AdvisorToneBox.SelectedItem = s.AdvisorTone;
        RoutineBudgetBox.ItemsSource = new[] { "20 minutes", "60 minutes", "2 hours", "Completionist" };
        RoutineBudgetBox.SelectedItem = s.RoutineBudget;
        BeginnerWarningsBox.IsChecked = s.BeginnerWarnings;
        AccentBox.Text = s.CustomAccent ?? "";
        ScaleSlider.Value = s.UiScalePct;
        ScaleLabel.Text = $"{s.UiScalePct}%";
        DockBox.SelectedIndex = s.ImmersiveDock switch { "Left" => 1, "Float" => 2, _ => 0 };
        WidthSlider.Value = s.ImmersiveWidthPct;
        WidthLabel.Text = s.ImmersiveWidthPct.ToString();
        OpacitySlider.Value = s.ImmersiveOpacityPct;
        OpacityLabel.Text = s.ImmersiveOpacityPct.ToString();
        AutoEnterBox.IsChecked = s.ImmersiveAutoEnter;
        TrayBox.IsChecked = s.MinimizeToTray;
        OverlayStartupBox.IsChecked = s.OverlayOnStartup;
        FoundryNotifyBox.IsChecked = s.FoundryNotifications;
        AutoDataRefreshBox.IsChecked = s.AutoRefreshData;
        AutoReturnBriefingBox.IsChecked = s.AutoReturnBriefing;
        DataRefreshCadenceBox.ItemsSource = new[] { "15 minutes", "30 minutes", "60 minutes", "120 minutes" };
        DataRefreshCadenceBox.SelectedItem = $"{s.DataRefreshMinutes} minutes";
        _suppressModelEvents = false;

        CallsignBox.Text = s.TennoCallsign ?? "";
        MrBox.Text = s.MasteryRank.ToString();
        PlatBox.Text = s.PlatinumOwned.ToString();
        NotesBox.Text = s.ProfileNotes ?? "";
        MarketUserBox.Text = s.MarketUsername ?? "";
        EeLogBox.Text = s.EeLogPath;
        KeyStatus.Text = s.HasApiKey ? "Key configured ✓" : "No key set.";
        MarketStatus.Text = s.MarketUsername is { Length: > 0 } u
            ? $"Account: {u}{(string.IsNullOrEmpty(s.MarketJwt) ? "" : " + access token")}"
            : "Not linked.";
        LogPathStatus.Text = File.Exists(s.EeLogPath) ? "File found ✓" : "File not found (start Warframe once).";
    }

    private static void FillModelBox(ComboBox box, string current)
    {
        var items = SettingsService.SuggestedModels.ToList();
        if (!items.Contains(current)) items.Insert(0, current);
        box.ItemsSource = items;
        box.SelectedItem = current;
    }

    // ---- appearance ----------------------------------------------------------

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressModelEvents || !IsLoaded || ThemeGallery.SelectedItem is not ThemePalette palette) return;
        var s = App.Services.Settings;
        s.ThemeName = palette.Name;
        s.CustomAccent = null; // selecting a preset previews the complete intended palette
        AccentBox.Text = "";
        ThemeService.ApplyFromSettings(s);
        AppearanceStatus.Text = $"{palette.Name} applied live. {palette.Description}";
    }

    private void OnResetAppearance(object sender, RoutedEventArgs e)
    {
        var s = App.Services.Settings;
        s.ThemeName = ThemeService.Verv.Name;
        s.CustomAccent = null;
        s.CompactMode = false;
        s.ReducedMotion = false;
        s.UiScalePct = 100;

        _suppressModelEvents = true;
        ThemeGallery.SelectedItem = ThemeService.Verv;
        AccentBox.Text = "";
        CompactModeBox.IsChecked = false;
        ReducedMotionBox.IsChecked = false;
        ScaleSlider.Value = 100;
        ScaleLabel.Text = "100%";
        _suppressModelEvents = false;

        ThemeService.ApplyFromSettings(s);
        (Application.Current.MainWindow as MainWindow)?.ApplyUiScale();
        AppearanceStatus.Text = "Appearance reset to Verv at 100% scale.";
    }

    private void OnStartupPageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressModelEvents || !IsLoaded || StartupPageBox.SelectedItem is not string label) return;
        if (StartupPages.TryGetValue(label, out var tag)) App.Services.Settings.StartupPage = tag;
    }

    private void OnPersonalizationChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressModelEvents || !IsLoaded) return;
        var s = App.Services.Settings;
        s.CompactMode = CompactModeBox.IsChecked == true;
        s.ReducedMotion = ReducedMotionBox.IsChecked == true;
        ThemeService.ApplyFromSettings(s);
        AppearanceStatus.Text = "Density and motion settings applied live across the complete interface.";
    }

    private void OnGuidanceChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressModelEvents || !IsLoaded) return;
        var s = App.Services.Settings;
        if (AdvisorToneBox.SelectedItem is string tone) s.AdvisorTone = tone;
        if (RoutineBudgetBox.SelectedItem is string budget) s.RoutineBudget = budget;
        s.BeginnerWarnings = BeginnerWarningsBox.IsChecked == true;
    }

    private void OnGuidanceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressModelEvents || !IsLoaded) return;
        OnGuidanceChanged(sender, new RoutedEventArgs());
    }

    private void OnApplyAccent(object sender, RoutedEventArgs e)
    {
        var hex = AccentBox.Text.Trim();
        if (!ThemeService.IsValidHex(hex)) { AppearanceStatus.Text = "Accent must be a valid #RRGGBB value."; return; }
        var s = App.Services.Settings;
        s.CustomAccent = hex;
        ThemeService.ApplyFromSettings(s);
        AppearanceStatus.Text = $"Custom accent {hex.ToUpperInvariant()} applied live.";
    }

    private void OnResetAccent(object sender, RoutedEventArgs e)
    {
        var s = App.Services.Settings;
        s.CustomAccent = null;
        AccentBox.Text = "";
        ThemeService.ApplyFromSettings(s);
        AppearanceStatus.Text = $"Using the native {ThemeService.Current.Name} accent.";
    }

    private void OnScaleChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressModelEvents || !IsLoaded) return;
        var pct = (int)ScaleSlider.Value;
        App.Services.Settings.UiScalePct = pct;
        ScaleLabel.Text = $"{pct}%";
        (Application.Current.MainWindow as MainWindow)?.ApplyUiScale();
        AppearanceStatus.Text = $"UI scale set to {pct}%.";
    }

    // ---- immersive + behavior --------------------------------------------------

    private void OnImmersiveOptionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressModelEvents || !IsLoaded) return;
        App.Services.Settings.ImmersiveDock =
            (DockBox.SelectedItem as ComboBoxItem)?.Content as string ?? "Right";
    }

    private void OnImmersiveSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressModelEvents || !IsLoaded) return;
        var s = App.Services.Settings;
        s.ImmersiveWidthPct = (int)WidthSlider.Value;
        s.ImmersiveOpacityPct = (int)OpacitySlider.Value;
        WidthLabel.Text = s.ImmersiveWidthPct.ToString();
        OpacityLabel.Text = s.ImmersiveOpacityPct.ToString();
    }

    private void OnBehaviorChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressModelEvents || !IsLoaded) return;
        var s = App.Services.Settings;
        s.ImmersiveAutoEnter = AutoEnterBox.IsChecked == true;
        s.MinimizeToTray = TrayBox.IsChecked == true;
        s.OverlayOnStartup = OverlayStartupBox.IsChecked == true;
        s.FoundryNotifications = FoundryNotifyBox.IsChecked == true;
    }

    // ---- API key -----------------------------------------------------------

    private void OnSaveKey(object sender, RoutedEventArgs e)
    {
        var key = ApiKeyBox.Password.Trim();
        if (key.Length == 0) { KeyStatus.Text = "Enter a key first."; return; }
        App.Services.Settings.ApiKey = key;
        ApiKeyBox.Password = "";
        KeyStatus.Text = "Key saved (encrypted) ✓";
    }

    private async void OnTestKey(object sender, RoutedEventArgs e)
    {
        if (!App.Services.Settings.HasApiKey) { KeyStatus.Text = "Save a key first."; return; }
        KeyStatus.Text = "Testing…";
        try
        {
            var reply = await App.Services.OpenRouter.TestAsync();
            KeyStatus.Text = $"Response: {(reply.Length > 40 ? reply[..40] : reply)} ✓";
        }
        catch (Exception ex)
        {
            KeyStatus.Text = $"Failed: {ex.Message}";
        }
    }

    // ---- models -------------------------------------------------------------

    private void OnModelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressModelEvents || !IsLoaded) return;
        var s = App.Services.Settings;
        if (ChatModelBox.SelectedItem is string c) s.ChatModel = c;
        if (BackgroundModelBox.SelectedItem is string b) s.BackgroundModel = b;
        if (VisionModelBox.SelectedItem is string v) s.VisionModel = v;
    }

    private void ApplyCustom(Action<string> setter)
    {
        var id = CustomModelBox.Text.Trim();
        if (id.Length == 0) { KeyStatus.Text = "Enter a custom model id first."; return; }
        setter(id);
        LoadCurrent();
    }

    private void OnCustomChat(object sender, RoutedEventArgs e) => ApplyCustom(m => App.Services.Settings.ChatModel = m);
    private void OnCustomBackground(object sender, RoutedEventArgs e) => ApplyCustom(m => App.Services.Settings.BackgroundModel = m);
    private void OnCustomVision(object sender, RoutedEventArgs e) => ApplyCustom(m => App.Services.Settings.VisionModel = m);

    // ---- profile / log path ---------------------------------------------------

    private void OnSaveProfile(object sender, RoutedEventArgs e)
    {
        var s = App.Services.Settings;
        s.TennoCallsign = CallsignBox.Text;
        if (int.TryParse(MrBox.Text, out var mr) && mr is >= 0 and <= 40) s.MasteryRank = mr;
        if (int.TryParse(PlatBox.Text, out var plat) && plat >= 0) s.PlatinumOwned = plat;
        ProfileStatus.Text = $"Saved: {(s.TennoCallsign is { Length: > 0 } n ? n + " · " : "")}MR {s.MasteryRank}, {s.PlatinumOwned} plat. The AI sees this on every call.";
    }

    private void OnSaveNotes(object sender, RoutedEventArgs e)
    {
        App.Services.Settings.ProfileNotes = NotesBox.Text;
        ProfileStatus.Text = $"Notes saved ({NotesBox.Text.Trim().Length} chars). Injected into every advisor call.";
    }

    // ---- warframe.market account ----------------------------------------------

    private void OnSaveMarket(object sender, RoutedEventArgs e)
    {
        var s = App.Services.Settings;
        s.MarketUsername = MarketUserBox.Text;
        if (MarketJwtBox.Password.Trim().Length > 0)
        {
            s.MarketJwt = MarketJwtBox.Password;
            MarketJwtBox.Password = "";
        }
        MarketStatus.Text = "Saved. Hit 'Test / sync now' to pull your orders.";
    }

    private async void OnTestMarket(object sender, RoutedEventArgs e)
    {
        var s = App.Services.Settings;
        s.MarketUsername = MarketUserBox.Text;
        if (MarketJwtBox.Password.Trim().Length > 0) { s.MarketJwt = MarketJwtBox.Password; MarketJwtBox.Password = ""; }

        var market = App.Services.Market;
        var slug = s.MarketUsername;

        // Access token present: validate it via /me and auto-fill the canonical profile slug.
        if (!string.IsNullOrEmpty(s.MarketJwt))
        {
            MarketStatus.Text = "Validating access token…";
            try
            {
                var me = await market.GetMeAsync(s.MarketJwt);
                var accountSlug = !string.IsNullOrWhiteSpace(me.Slug) ? me.Slug : me.IngameName;
                if (accountSlug.Length > 0 && string.IsNullOrWhiteSpace(slug))
                {
                    s.MarketUsername = accountSlug;
                    MarketUserBox.Text = accountSlug;
                    slug = accountSlug;
                }
                MarketStatus.Text = $"Token OK — {me.IngameName} (rep {me.Reputation}). ";
            }
            catch (Exception ex)
            {
                MarketStatus.Text = $"Token failed (expired? re-copy it): {ex.Message}. ";
            }
        }

        if (string.IsNullOrWhiteSpace(slug)) { MarketStatus.Text += "Enter your warframe.market profile slug or in-game name to sync orders."; return; }

        try
        {
            var orders = await market.GetUserOrdersAsync(slug);
            MarketStatus.Text += $"Synced {orders.Count} orders " +
                $"({orders.Count(o => o.OrderType == "sell")} sell / {orders.Count(o => o.OrderType == "buy")} buy). " +
                "See them in Market & Trading.";
        }
        catch (Exception ex)
        {
            MarketStatus.Text += $"Order sync failed: {ex.Message}";
        }
    }

    private void OnSaveLogPath(object sender, RoutedEventArgs e)
    {
        var path = EeLogBox.Text.Trim();
        if (path.Length == 0) return;
        App.Services.Settings.EeLogPath = path;
        LogPathStatus.Text = File.Exists(path) ? "File found ✓" : "Saved, but file not found yet.";
    }

    // ---- data ------------------------------------------------------------------

    private void OnDataAutomationChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressModelEvents || !IsLoaded) return;
        var s = App.Services.Settings;
        s.AutoRefreshData = AutoDataRefreshBox.IsChecked == true;
        s.AutoReturnBriefing = AutoReturnBriefingBox.IsChecked == true;
        DataStatus.Text = s.AutoRefreshData
            ? "Automatic refresh enabled. Cached data remains available during outages."
            : "Automatic refresh disabled. Manual refresh remains available.";
    }

    private void OnDataCadenceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressModelEvents || !IsLoaded || DataRefreshCadenceBox.SelectedItem is not string label) return;
        if (int.TryParse(label.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0], out var minutes))
        {
            App.Services.Settings.DataRefreshMinutes = minutes;
            DataStatus.Text = $"Refresh check cadence set to {minutes} minutes. Per-source TTLs prevent unnecessary downloads.";
        }
    }

    private async void OnRefreshAllData(object sender, RoutedEventArgs e)
    {
        DataStatus.Text = "Refreshing official item data + warframe.market caches…";
        try
        {
            var result = await App.Services.Intelligence.RefreshAllAsync(force: true);
            DataStatus.Text = $"Live data ready: {result.GameItems:N0} game items, {result.MarketItems:N0} tradables, {result.Prices:N0} prices. FTS: {(App.Services.Db.FtsAvailable ? "enabled" : "fallback index")}.";
        }
        catch (Exception ex) { DataStatus.Text = $"Refresh failed; previous cache retained: {ex.Message}"; }
    }

    private async void OnRefreshCatalog(object sender, RoutedEventArgs e)
    {
        DataStatus.Text = "Fetching warframe.market item catalog…";
        try { DataStatus.Text = $"Catalog: {await App.Services.Market.EnsureItemCatalogAsync(force: true):N0} items cached."; }
        catch (Exception ex) { DataStatus.Text = $"Failed: {ex.Message}"; }
    }

    private async void OnRefreshPrices(object sender, RoutedEventArgs e)
    {
        DataStatus.Text = "Fetching bulk price snapshot…";
        try
        {
            var market = App.Services.Market;
            var snap = await market.GetPriceSnapshotAsync(force: true);
            var priced = snap.Values.Count(x => x.WaPrice > 0 || x.Median > 0);
            DataStatus.Text = market.LastSnapshotWarning is { Length: > 0 } warning
                ? $"Market metadata ready ({snap.Count:N0} items); {priced:N0} cached bulk prices. {warning}"
                : $"Prices: {priced:N0} items with bulk Platinum data; {snap.Count:N0} items with Ducat metadata.";
        }
        catch (Exception ex) { DataStatus.Text = $"Failed: {ex.Message}"; }
    }

    private async void OnRefreshMastery(object sender, RoutedEventArgs e)
    {
        DataStatus.Text = "Syncing mastery catalog…";
        try { DataStatus.Text = $"Mastery: {await App.Services.Mastery.SyncCatalogAsync():N0} items synced."; }
        catch (Exception ex) { DataStatus.Text = $"Failed: {ex.Message}"; }
    }

    private void OnOpenDataFolder(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(Db.AppDataDir) { UseShellExecute = true });

    private void OnClearAiCache(object sender, RoutedEventArgs e)
    {
        App.Services.Db.Exec("DELETE FROM ai_cache");
        DataStatus.Text = "AI response cache cleared.";
    }

    private void OnOptimizeDatabase(object sender, RoutedEventArgs e)
    {
        App.Services.Db.Checkpoint();
        DataStatus.Text = "SQLite WAL checkpointed and indexes optimized.";
    }
}
