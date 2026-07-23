using System.Windows;
using System.Windows.Controls;
using CephalonCuda.Models;
using CephalonCuda.Services;

namespace CephalonCuda.Views;

public partial class MasteryView : UserControl
{
    private List<MasteryItem> _items = [];

    public MasteryView()
    {
        InitializeComponent();
        Loaded += (_, _) => Reload();
    }

    private void Reload()
    {
        _items = App.Services.Mastery.GetAll();
        ApplyFilter();
        RefreshStats();
    }

    private void RefreshStats()
    {
        var (xp, mr, mastered, total) = App.Services.Mastery.Stats();
        MrText.Text = mr.ToString();
        XpText.Text = xp.ToString("N0");
        CountText.Text = $"{mastered:N0} / {total:N0}";

        var userMr = Math.Max(App.Services.Settings.MasteryRank, mr);
        SuggestList.ItemsSource = App.Services.Mastery.Suggestions(userMr, 40)
            .Select(s => $"MR{s.MasteryReq,2} · {s.Name} ({s.Type ?? s.Kind})")
            .ToList();
    }

    private void ApplyFilter()
    {
        IEnumerable<MasteryItem> view = _items;
        var q = FilterBox.Text.Trim();
        if (q.Length > 0) view = view.Where(i => i.Name.Contains(q, StringComparison.OrdinalIgnoreCase));
        if (KindBox.SelectedIndex == 1) view = view.Where(i => i.Kind == "Warframe");
        if (KindBox.SelectedIndex == 2) view = view.Where(i => i.Kind == "Weapon");
        if (UnmasteredOnly.IsChecked == true) view = view.Where(i => !i.Mastered);
        ItemGrid.ItemsSource = view.ToList();
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e) => ApplyFilter();
    private void OnFilterOptionChanged(object sender, RoutedEventArgs e) { if (IsLoaded) ApplyFilter(); }

    private async void OnSync(object sender, RoutedEventArgs e)
    {
        SuggestStatus.Text = "Syncing catalog from api.warframestat.us…";
        try
        {
            var n = await App.Services.Mastery.SyncCatalogAsync();
            SuggestStatus.Text = $"Synced {n:N0} items.";
            Reload();
        }
        catch (Exception ex)
        {
            SuggestStatus.Text = $"Sync failed: {ex.Message}";
        }
    }

    private void OnMasteredToggled(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { Tag: MasteryItem item } cb)
        {
            item.Mastered = cb.IsChecked == true;
            App.Services.Mastery.SetMastered(item.Name, item.Mastered);
            RefreshStats();
        }
    }

    // ---- Scan Profile Screen via AI Vision -----------------------------------

    private async void OnScanProfile(object sender, RoutedEventArgs e)
    {
        var s = App.Services;
        if (!s.Settings.HasApiKey)
        {
            SuggestStatus.Text = "No API key — add one in Settings.";
            return;
        }

        SuggestStatus.Text = "📷 Capturing screen…";

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
            SuggestStatus.Text = "Capture failed — is the game in Borderless/Windowed?";
            return;
        }

        var b64 = ScreenCaptureService.ToBase64Png(frame);
        SuggestStatus.Text = "🧠 Analyzing profile screen…";

        var messages = new List<ChatMsg>
        {
            s.Context.BuildSystemMessage("Warframe mastery rank progression equipment mastery requirements and efficient leveling"),
            new ChatMsg
            {
                Role = "user",
                ImageBase64Png = b64,
                Content = """
                    Analyze this Warframe profile, arsenal, or equipment screen.
                    Extract ALL items that show as MASTERED (rank 30 / fully leveled).
                    Item names must match the in-game names exactly (e.g. "Excalibur Prime", "Boltor Prime").

                    Return ONLY a cuda-action JSON array with mark_mastered actions. Example:
                    ```cuda-action
                    [{"action":"mark_mastered","names":["Excalibur","Mag","Volt"]}]
                    ```

                    If you cannot identify any mastered items, return an empty array: []
                    """,
            },
        };

        try
        {
            var response = await s.OpenRouter.CompleteAsync(messages, s.Settings.VisionModel);
            var actions = s.AiCommands.ParseActions(response);

            if (actions.Count == 0)
            {
                SuggestStatus.Text = "No mastered items detected — make sure the profile screen is visible.";
                return;
            }

            SuggestStatus.Text = $"Found items — confirm to mark mastered…";
            var dialog = new AiActionDialog(actions, s.AiCommands)
            {
                Owner = Window.GetWindow(this),
            };
            if (dialog.ShowDialog() == true && dialog.Results.Count > 0)
            {
                var ok = dialog.Results.Count(r => r.Success);
                SuggestStatus.Text = $"✓ Applied {ok}/{dialog.Results.Count} changes.";
                Reload();
            }
            else
            {
                SuggestStatus.Text = "Scan cancelled.";
            }
        }
        catch (Exception ex)
        {
            SuggestStatus.Text = $"Scan failed: {ex.Message}";
        }
    }
}
