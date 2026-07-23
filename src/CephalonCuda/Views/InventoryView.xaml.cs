using System.Windows;
using System.Windows.Controls;
using CephalonCuda.Models;

namespace CephalonCuda.Views;

public partial class InventoryView : UserControl
{
    private List<InventoryItem> _items = [];

    public InventoryView()
    {
        InitializeComponent();
        Loaded += async (_, _) => await ReloadAsync();
    }

    private async Task ReloadAsync(bool forcePrices = false)
    {
        try
        {
            if (forcePrices) await App.Services.Market.GetPriceSnapshotAsync(force: true);
            _items = await App.Services.Inventory.GetAllAsync();
            ApplyFilter();
            var (plat, ducats, count) = App.Services.Inventory.Totals(_items);
            TotalPlatText.Text = $"{plat:0} p";
            TotalDucatsText.Text = $"{ducats:N0} d";
            ItemCountText.Text = count.ToString("N0");
        }
        catch (Exception ex)
        {
            AddStatus.Text = $"Load failed: {ex.Message}";
        }
    }

    private void ApplyFilter()
    {
        var q = FilterBox.Text.Trim();
        ItemsGrid.ItemsSource = string.IsNullOrEmpty(q)
            ? _items
            : _items.Where(i => i.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private async void OnRefreshPrices(object sender, RoutedEventArgs e)
    {
        AddStatus.Text = "Refreshing prices…";
        await ReloadAsync(forcePrices: true);
        if (AddStatus.Text.StartsWith("Load failed", StringComparison.Ordinal)) return;
        AddStatus.Text = App.Services.Market.LastSnapshotWarning is { Length: > 0 } warning
            ? $"Using cached/metadata-only values. {warning}"
            : "Prices refreshed.";
    }

    private void OnNameChanged(object sender, TextChangedEventArgs e)
    {
        var q = NameBox.Text.Trim();
        if (q.Length < 3)
        {
            SuggestionList.Visibility = Visibility.Collapsed;
            return;
        }
        var matches = App.Services.Market.SearchItems(q, 8).Select(m => m.ItemName).ToList();
        SuggestionList.ItemsSource = matches;
        SuggestionList.Visibility = matches.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSuggestionPicked(object sender, RoutedEventArgs e)
    {
        if (SuggestionList.SelectedItem is string name)
        {
            NameBox.TextChanged -= OnNameChanged;
            NameBox.Text = name;
            NameBox.TextChanged += OnNameChanged;
            SuggestionList.Visibility = Visibility.Collapsed;
        }
    }

    private async void OnAdd(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0) { AddStatus.Text = "Enter an item name."; return; }
        if (!int.TryParse(QtyBox.Text.Trim(), out var qty) || qty < 0) { AddStatus.Text = "Bad quantity."; return; }

        App.Services.Inventory.Upsert(name, qty);
        NameBox.Text = "";
        QtyBox.Text = "1";
        SuggestionList.Visibility = Visibility.Collapsed;
        AddStatus.Text = $"Saved {name} x{qty}.";
        await ReloadAsync();
    }

    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: long id })
        {
            App.Services.Inventory.Delete(id);
            await ReloadAsync();
        }
    }
}
