using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CephalonCuda.Models;
using CephalonCuda.Services;

namespace CephalonCuda.Views;

public partial class RelicPlannerView : UserControl
{
    private static readonly string[] Refinements = ["Intact", "Exceptional", "Flawless", "Radiant"];
    private List<RelicInfo> _relics = [];
    private bool _loading;

    public RelicPlannerView()
    {
        InitializeComponent();
        RefinementBox.ItemsSource = Refinements;
        RefinementBox.SelectedIndex = 0;
        Loaded += async (_, _) => { if (_relics.Count == 0) await ReloadAsync(); };
    }

    private async Task ReloadAsync(bool forcePrices = false)
    {
        if (_loading) return;
        _loading = true;
        StatusText.Text = "Computing relic values from live prices…";
        try
        {
            var market = App.Services.Market;
            await market.GetPriceSnapshotAsync(force: forcePrices);
            var state = RefinementBox.SelectedItem as string ?? "Intact";
            _relics = await App.Services.RelicData.GetValuedRelicsAsync(state, market.LookupPrice);
            ApplyFilter();
            StatusText.Text = market.LastSnapshotWarning is { Length: > 0 } warning
                ? $"{_relics.Count} relics loaded ({state}); bulk price ranking may be incomplete. {warning}"
                : $"{_relics.Count} relics ranked by expected platinum ({state}).";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Failed: {ex.Message}";
        }
        finally { _loading = false; }
    }

    private void ApplyFilter()
    {
        var q = SearchBox.Text.Trim();
        RelicGrid.ItemsSource = string.IsNullOrEmpty(q)
            ? _relics
            : _relics.Where(r => r.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private async void OnRefinementChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) await ReloadAsync();
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private async void OnRecompute(object sender, RoutedEventArgs e) => await ReloadAsync(forcePrices: true);

    private void OnRelicSelected(object sender, SelectionChangedEventArgs e)
    {
        if (RelicGrid.SelectedItem is not RelicInfo relic) return;
        DetailHeader.Text = $"{relic.DisplayName} ({relic.State}) — EV {relic.ExpectedPlat:0.0}p";
        RewardGrid.ItemsSource = relic.Rewards.OrderByDescending(r => r.Plat).ToList();

        var ws = App.Services.WorldState.Current;
        var fissures = ws?.Fissures
            .Where(f => !f.Expired && string.Equals(f.Tier, relic.Tier, StringComparison.OrdinalIgnoreCase))
            .Select(f => $"{f.MissionType} @ {f.Node} — {(f.IsStorm ? "Storm · " : "")}{(f.IsHard ? "SP · " : "")}{f.Eta}")
            .ToList();
        FissureList.ItemsSource = fissures is { Count: > 0 }
            ? fissures
            : new List<string> { ws is null ? "World state unavailable." : $"No {relic.Tier} fissures active right now." };
    }

    // ---- Scan Relics Screen via AI Vision ------------------------------------

    private async void OnScanRelics(object sender, RoutedEventArgs e)
    {
        var s = App.Services;
        if (!s.Settings.HasApiKey)
        {
            StatusText.Text = "No API key — add one in Settings.";
            return;
        }

        StatusText.Text = "📷 Capturing screen…";

        var mainWin = Application.Current.MainWindow;
        bool hidden = false;
        if (s.Tracker.GameRunning && mainWin is not null && mainWin.IsVisible)
        {
            mainWin.Hide();
            await Task.Delay(250);
            hidden = true;
        }

        var frame = s.Capture.CaptureRegion(s.Tracker.CaptureBounds);

        if (hidden && mainWin is not null)
        {
            mainWin.Show();
            mainWin.Activate();
        }

        if (frame is null)
        {
            StatusText.Text = "Capture failed — is the game in Borderless/Windowed?";
            return;
        }

        var b64 = ScreenCaptureService.ToBase64Png(frame);
        StatusText.Text = "🧠 Analyzing relic inventory…";

        var messages = new List<ChatMsg>
        {
            s.Context.BuildSystemMessage("Warframe relic rewards refinement expected value farming and active fissures"),
            new ChatMsg
            {
                Role = "user",
                ImageBase64Png = b64,
                Content = """
                    Analyze this Warframe relic inventory/management screen. Extract ALL relics you can see.
                    For each relic, provide the full name (e.g. "Lith A1", "Neo B3", "Axi L4") and the quantity.
                    Add " Relic" suffix to the name for inventory tracking.

                    Return ONLY a cuda-action JSON array with add_inventory actions. Example:
                    ```cuda-action
                    [
                      {"action":"add_inventory","items":[
                        {"name":"Lith A1 Relic","quantity":3},
                        {"name":"Meso B4 Relic","quantity":1},
                        {"name":"Neo N8 Relic","quantity":7}
                      ]}
                    ]
                    ```

                    If you cannot identify any relics, return an empty array: []
                    """,
            },
        };

        try
        {
            var response = await s.OpenRouter.CompleteAsync(messages, s.Settings.VisionModel);
            var actions = s.AiCommands.ParseActions(response);

            if (actions.Count == 0)
            {
                StatusText.Text = "No relics detected — make sure the relic inventory is visible.";
                return;
            }

            StatusText.Text = $"Found relics — confirm to add to inventory…";
            var dialog = new AiActionDialog(actions, s.AiCommands)
            {
                Owner = Window.GetWindow(this),
            };
            if (dialog.ShowDialog() == true && dialog.Results.Count > 0)
            {
                var ok = dialog.Results.Count(r => r.Success);
                StatusText.Text = $"✓ Added {ok} relic entries to inventory.";
            }
            else
            {
                StatusText.Text = "Scan cancelled.";
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Scan failed: {ex.Message}";
        }
    }

    // ---- AI Relic Recommendations --------------------------------------------

    private async void OnSuggest(object sender, RoutedEventArgs e)
    {
        var s = App.Services;
        if (!s.Settings.HasApiKey)
        {
            StatusText.Text = "No API key — add one in Settings.";
            return;
        }

        StatusText.Text = "💡 Asking Cuda for relic advice…";

        // Build a context-rich query including top relic EV data.
        var topRelics = _relics.Take(15)
            .Select(r => $"{r.DisplayName} EV={r.ExpectedPlat:0.0}p (best: {r.BestItem} {r.BestPlat:0}p)")
            .ToList();
        var ws = s.WorldState.Current;
        var activeFissures = ws?.Fissures
            .Where(f => !f.Expired)
            .Select(f => $"{f.Tier} {f.MissionType} @ {f.Node}{(f.IsHard ? " SP" : "")}{(f.IsStorm ? " Storm" : "")} ({f.Eta})")
            .ToList() ?? [];

        var messages = new List<ChatMsg>
        {
            s.Context.BuildSystemMessage("Warframe relic rewards refinement expected value farming and active fissures"),
            new ChatMsg
            {
                Role = "user",
                Content = $"""
                    I want advice on which relics to focus on running right now.

                    My current refinement setting: {RefinementBox.SelectedItem ?? "Intact"}

                    TOP RELICS BY EXPECTED PLATINUM ({RefinementBox.SelectedItem ?? "Intact"}):
                    {string.Join("\n", topRelics)}

                    ACTIVE FISSIONS RIGHT NOW:
                    {(activeFissures.Count > 0 ? string.Join("\n", activeFissures) : "None available.")}

                    Based on my inventory, mastery progress, foundry queue, and the data above:
                    1. Which relics should I prioritize running and why?
                    2. What refinement level should I use?
                    3. Are there any active fissures I should jump on?
                    4. Any specific prime parts I should target?

                    Keep it concise and actionable. Use bullet points.
                    """,
            },
        };

        try
        {
            var response = await s.OpenRouter.CompleteAsync(messages, s.Settings.ChatModel);
            StatusText.Text = "Recommendation ready.";

            // Show the recommendation in a simple dialog.
            var textBrush = (Brush)TryFindResource("Brush.Text") ?? Brushes.White;
            var bgBrush = (Brush)TryFindResource("Brush.Bg") ?? Brushes.Black;
            var dlg = new Window
            {
                Title = "Cephalon Cuda — Relic Recommendations",
                Width = 560, Height = 440,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this),
                Background = bgBrush,
            };
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(16) };
            var text = new TextBlock
            {
                Text = response,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Foreground = textBrush,
            };
            scroll.Content = text;
            dlg.Content = scroll;
            dlg.ShowDialog();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Failed: {ex.Message}";
        }
    }
}
