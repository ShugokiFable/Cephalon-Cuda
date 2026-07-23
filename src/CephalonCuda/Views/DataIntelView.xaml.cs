using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CephalonCuda.Models;
using Microsoft.Win32;

namespace CephalonCuda.Views;

public partial class DataIntelView : UserControl
{
    private KnowledgeHit? _selected;

    public DataIntelView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            RefreshSources();
            FeedUrlBox.Text = App.Services.Settings.CommunityFeedUrl ?? "";
            SearchBox.Text = "Saryn build";
            RunSearch();
        };
    }

    private void RefreshSources() => SourcesList.ItemsSource = App.Services.Intelligence.GetSourceHealth();

    private async void OnRefreshAll(object sender, RoutedEventArgs e)
    {
        DataStatus.Text = "Refreshing WFCD item data + warframe.market catalog and prices…";
        SetActionsEnabled(false);
        try
        {
            var result = await App.Services.Intelligence.RefreshAllAsync(force: true);
            DataStatus.Text = $"Live refresh complete: {result.GameItems:N0} game items, {result.MarketItems:N0} tradables, {result.Prices:N0} price rows.";
            RefreshSources();
            RunSearch();
        }
        catch (Exception ex)
        {
            DataStatus.Text = $"Refresh failed; existing cache was kept: {ex.Message}";
            RefreshSources();
        }
        finally { SetActionsEnabled(true); }
    }

    private void OnSaveFeed(object sender, RoutedEventArgs e)
    {
        var url = FeedUrlBox.Text.Trim();
        if (url.Length > 0 && (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps))
        {
            DataStatus.Text = "Feed not saved. Use an absolute HTTPS JSON URL.";
            return;
        }
        App.Services.Settings.CommunityFeedUrl = url;
        DataStatus.Text = url.Length == 0
            ? "Licensed community feed disabled. Manual JSON imports remain available."
            : "Licensed community feed saved; it refreshes at startup and on demand.";
    }

    private async void OnRefreshFeed(object sender, RoutedEventArgs e)
    {
        var url = FeedUrlBox.Text.Trim();
        if (url.Length == 0)
        {
            DataStatus.Text = "Enter an HTTPS JSON feed first.";
            return;
        }
        App.Services.Settings.CommunityFeedUrl = url;
        DataStatus.Text = "Refreshing licensed community feed…";
        try
        {
            var result = await App.Services.Intelligence.RefreshCommunityFeedAsync(url);
            DataStatus.Text = $"Feed refreshed: {result.TierCount:N0} tiers + {result.BuildCount:N0} builds from {result.SourceName}.";
            RefreshSources();
            RunSearch();
        }
        catch (Exception ex)
        {
            DataStatus.Text = $"Feed refresh failed; previous import retained: {ex.Message}";
            RefreshSources();
        }
    }

    private void OnImportCommunity(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import licensed or manually curated tier/build data",
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var result = App.Services.Intelligence.ImportCommunityIntel(dialog.FileName);
            DataStatus.Text = $"Imported {result.TierCount:N0} tiers + {result.BuildCount:N0} builds from {result.SourceName}.";
            RefreshSources();
            RunSearch();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Community data import failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            DataStatus.Text = "Import rejected. The existing database was not changed.";
        }
    }

    private void OnExportTemplate(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export community intel JSON template",
            FileName = "cephalon-cuda-community-intel-template.json",
            Filter = "JSON files (*.json)|*.json",
            AddExtension = true,
        };
        if (dialog.ShowDialog() != true) return;
        App.Services.Intelligence.ExportCommunityTemplate(dialog.FileName);
        DataStatus.Text = $"Template exported to {dialog.FileName}";
    }

    private void OnClearCommunity(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Remove all imported community tiers and builds? Official/live caches are untouched.",
                "Clear community intel", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        App.Services.Intelligence.ClearCommunityIntel();
        DataStatus.Text = "Community imports cleared.";
        RefreshSources();
        RunSearch();
    }

    private void OnSearch(object sender, RoutedEventArgs e) => RunSearch();

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        RunSearch();
        e.Handled = true;
    }

    private void RunSearch()
    {
        var query = SearchBox.Text.Trim();
        ResultsGrid.ItemsSource = App.Services.Intelligence.Search(query, 80);
        DataStatus.Text = query.Length == 0
            ? "Showing local item database. Enter a mechanics, item, build, or farming question to search all sources."
            : $"Indexed results for “{query}”. The advisor retrieves from this same database per question.";
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selected = ResultsGrid.SelectedItem as KnowledgeHit;
        SelectedTitle.Text = _selected is null ? "Select a result for details." : $"{_selected.SourceDisplay} · {_selected.Name}";
        SelectedSummary.Text = _selected?.Summary ?? "";
        OpenSourceButton.IsEnabled = IsSafeWebUrl(_selected?.SourceUrl);
    }

    private void OnOpenSelectedSource(object sender, RoutedEventArgs e)
    {
        if (_selected?.SourceUrl is not { Length: > 0 } url) return;
        OpenUrl(url);
    }

    private void OnOpenOverframeTiers(object sender, RoutedEventArgs e) => OpenUrl("https://overframe.gg/tier-list/warframes/");
    private void OnOpenOverframeBuilds(object sender, RoutedEventArgs e) => OpenUrl("https://overframe.gg/builds/");

    private static void OpenUrl(string url)
    {
        if (!IsSafeWebUrl(url)) return;
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private static bool IsSafeWebUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
         string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase));

    private void SetActionsEnabled(bool enabled)
    {
        SearchBox.IsEnabled = enabled;
        ResultsGrid.IsEnabled = enabled;
    }
}
