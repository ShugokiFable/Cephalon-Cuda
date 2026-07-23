using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CephalonCuda.Models;
using CephalonCuda.Services;

namespace CephalonCuda.Views;

public partial class FoundryView : UserControl
{
    private readonly DispatcherTimer _tick;

    public sealed record JobRow(long Id, string Name, string Info, string Remaining, Brush RemainingBrush);

    public FoundryView()
    {
        InitializeComponent();
        PresetBox.ItemsSource = FoundryService.Presets.Select(p => p.Label).ToList();
        PresetBox.SelectedIndex = 2;

        _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += (_, _) => Reload();
        Loaded += (_, _) => { Reload(); _tick.Start(); };
        Unloaded += (_, _) => _tick.Stop();
    }

    private void Reload()
    {
        var accent = (Brush)FindResource("Brush.Accent");
        var text = (Brush)FindResource("Brush.Text");
        JobList.ItemsSource = App.Services.Foundry.GetAll(includeClaimed: ShowClaimed.IsChecked == true)
            .Select(j => new JobRow(
                j.Id, j.Name,
                $"{j.DurationMinutes / 60.0:0.#}h build · done {j.EndsAt.ToLocalTime():ddd HH:mm}{(j.Claimed ? " · claimed" : "")}",
                j.Claimed ? "CLAIMED" : j.RemainingDisplay,
                j.IsReady && !j.Claimed ? accent : text))
            .ToList();
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        var name = JobName.Text.Trim();
        if (name.Length == 0) { AddStatus.Text = "Name the build."; return; }

        int minutes;
        if (int.TryParse(CustomMinutes.Text.Trim(), out var custom) && custom > 0)
            minutes = custom;
        else if (PresetBox.SelectedIndex >= 0)
            minutes = FoundryService.Presets[PresetBox.SelectedIndex].Minutes;
        else { AddStatus.Text = "Pick a duration."; return; }

        App.Services.Foundry.Add(name, minutes);
        JobName.Text = CustomMinutes.Text = "";
        AddStatus.Text = $"Timer started: {name} ({minutes / 60.0:0.#}h).";
        Reload();
    }

    private void OnShowClaimedChanged(object sender, RoutedEventArgs e) { if (IsLoaded) Reload(); }

    private void OnClaim(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: long id }) { App.Services.Foundry.Claim(id); Reload(); }
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: long id }) { App.Services.Foundry.Delete(id); Reload(); }
    }

    // ---- Scan Foundry Screen via AI Vision -----------------------------------

    private async void OnScanFoundry(object sender, RoutedEventArgs e)
    {
        var s = App.Services;
        if (!s.Settings.HasApiKey)
        {
            AddStatus.Text = "No API key — add one in Settings.";
            return;
        }

        AddStatus.Text = "📷 Capturing screen…";

        // Hide the main window briefly so the capture shows the game, not us.
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
            AddStatus.Text = "Capture failed — is the game in Borderless/Windowed?";
            return;
        }

        var b64 = ScreenCaptureService.ToBase64Png(frame);
        AddStatus.Text = "🧠 Analyzing foundry screen…";

        var messages = new List<ChatMsg>
        {
            s.Context.BuildSystemMessage("Warframe foundry crafting times components and build requirements"),
            new ChatMsg
            {
                Role = "user",
                ImageBase64Png = b64,
                Content = """
                    Analyze this Warframe foundry screen. Extract ALL items currently being crafted.
                    For each item, provide the exact item name as shown and the remaining build time in total minutes.
                    If a build is complete/ready, use duration_minutes: 0.

                    Return ONLY a cuda-action JSON array, nothing else. Example:
                    ```cuda-action
                    [{"action":"add_foundry_job","name":"Saryn Prime Neuroptics","duration_minutes":480}]
                    ```

                    If you cannot identify any foundry items, return an empty array: []
                    """,
            },
        };

        try
        {
            var response = await s.OpenRouter.CompleteAsync(messages, s.Settings.VisionModel);
            var actions = s.AiCommands.ParseActions(response);

            if (actions.Count == 0)
            {
                AddStatus.Text = "No items detected — make sure the foundry screen is visible.";
                return;
            }

            AddStatus.Text = $"Found {actions.Count} item(s) — confirm to add…";
            var dialog = new AiActionDialog(actions, s.AiCommands)
            {
                Owner = Window.GetWindow(this),
            };
            if (dialog.ShowDialog() == true && dialog.Results.Count > 0)
            {
                var ok = dialog.Results.Count(r => r.Success);
                AddStatus.Text = $"✓ Added {ok}/{dialog.Results.Count} builds.";
                Reload();
            }
            else
            {
                AddStatus.Text = "Scan cancelled.";
            }
        }
        catch (Exception ex)
        {
            AddStatus.Text = $"Scan failed: {ex.Message}";
        }
    }
}
