using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using CephalonCuda.Models;

namespace CephalonCuda.Views;

public partial class MarketView : UserControl
{
    private CancellationTokenSource? _searchCts;
    private List<PricePoint> _chartData = [];
    private string? _selectedUrl;
    private List<MarketOrder>? _allOrders;
    private ItemDetails? _currentDetails;

    public MarketView()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            RefreshLedger();
            RefreshMyOrders();
            try { await App.Services.Market.EnsureItemCatalogAsync(); }
            catch (Exception ex) { ItemMeta.Text = $"Catalog unavailable: {ex.Message}"; }
        };
    }

    // ---- my warframe.market orders -----------------------------------------

    private void RefreshMyOrders()
    {
        var market = App.Services.Market;
        if (market.MyOrders.Count > 0)
        {
            MyOrdersGrid.ItemsSource = market.MyOrders;
            OrdersStatus.Text = $"{market.MyOrders.Count} orders · synced {market.AccountSyncedAt?.ToLocalTime():HH:mm}" +
                (market.Me is { } me ? $" · {me.IngameName} (rep {me.Reputation})" : "");
        }
    }

    private async void OnSyncOrders(object sender, RoutedEventArgs e)
    {
        var s = App.Services.Settings;
        if (string.IsNullOrWhiteSpace(s.MarketUsername))
        {
            OrdersStatus.Text = "No account linked — add your in-game name in Settings first.";
            return;
        }
        OrdersStatus.Text = "Syncing…";
        try
        {
            var orders = await App.Services.Market.GetUserOrdersAsync(s.MarketUsername);
            MyOrdersGrid.ItemsSource = orders;
            OrdersStatus.Text = $"{orders.Count} orders " +
                $"({orders.Count(o => o.OrderType == "sell")} sell / {orders.Count(o => o.OrderType == "buy")} buy). " +
                "MINE = your price, MKT = current prime-part reference.";
        }
        catch (Exception ex)
        {
            OrdersStatus.Text = $"Sync failed: {ex.Message}";
        }
    }

    // ---- lookup -------------------------------------------------------------

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        var q = SearchBox.Text.Trim();
        if (q.Length < 3) { ResultList.ItemsSource = null; ResultList.Visibility = Visibility.Collapsed; return; }
        var items = App.Services.Market.SearchItems(q, 20);
        ResultList.ItemsSource = items;
        ResultList.DisplayMemberPath = "ItemName";
        ResultList.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnItemSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ResultList.SelectedItem is not MarketItem item) return;

        // Dismiss the autocomplete dropdown
        ResultList.ItemsSource = null;
        ResultList.Visibility = Visibility.Collapsed;

        _selectedUrl = item.UrlName;
        ItemMeta.Text = "Loading…";
        ChartTitle.Text = $"90-DAY MEDIAN — {item.ItemName.ToUpperInvariant()}";
        SellGrid.ItemsSource = BuyGrid.ItemsSource = null;
        RankFilterPanel.Visibility = Visibility.Collapsed;

        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;
        try
        {
            var market = App.Services.Market;
            var details = await market.GetItemDetailsAsync(item.UrlName, ct);
            var orders = await market.GetOrdersAsync(item.UrlName, ct);
            string? chartWarning = null;
            try { _chartData = await market.GetStatisticsAsync(item.UrlName, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _chartData = [];
                chartWarning = $"Historical chart unavailable; live v2 orders are still current. {ex.Message}";
            }
            if (ct.IsCancellationRequested || _selectedUrl != item.UrlName) return;

            _allOrders = orders;
            _currentDetails = details;

            // Detect rankability: primary from item details, fallback from orders
            var rankable = details?.IsRankable == true
                || orders.Any(o => o.ModRank.HasValue);

            if (rankable)
            {
                var ranks = orders
                    .Where(o => o.ModRank.HasValue)
                    .Select(o => o.ModRank!.Value)
                    .Distinct()
                    .OrderBy(r => r)
                    .ToList();
                if (ranks.Count > 0)
                {
                    RankFilterCombo.SelectionChanged -= OnRankFilterChanged;
                    RankFilterCombo.ItemsSource = null;
                    var items2 = new List<string> { "All" };
                    items2.AddRange(ranks.Select(r => r == 0 ? "0 (Unranked)" : r.ToString()));
                    RankFilterCombo.ItemsSource = items2;
                    RankFilterCombo.SelectedIndex = 0;
                    RankFilterCombo.SelectionChanged += OnRankFilterChanged;
                    RankFilterPanel.Visibility = Visibility.Visible;
                }
                else
                {
                    RankFilterPanel.Visibility = Visibility.Collapsed;
                }
            }
            else
            {
                RankFilterPanel.Visibility = Visibility.Collapsed;
            }

            ApplyOrderFilter();

            ItemMeta.Text =
                $"{item.ItemName}" +
                (details?.Ducats is { } d ? $" · {d} ducats" : "") +
                (details?.Vaulted is { } v ? v ? " · VAULTED" : " · unvaulted" : "") +
                (details?.MasteryLevel is { } m ? $" · MR {m}" : "") +
                (details?.TradingTax is { } t ? $" · tax {t:N0}cr" : "") +
                (details?.MaxRank is { } mr ? $" · max rank {mr}" : "");
            RenderChart();
            if (chartWarning is not null) ChartInfo.Text = chartWarning;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ItemMeta.Text = $"Lookup failed: {ex.Message}";
        }
    }

    private void OnRankFilterChanged(object sender, SelectionChangedEventArgs e) => ApplyOrderFilter();

    private void ApplyOrderFilter()
    {
        if (_allOrders is null) return;

        // Detect rankability from details OR from orders having rank data
        var rankable = _currentDetails?.IsRankable == true
            || _allOrders.Any(o => o.ModRank.HasValue);
        var selectedRank = -1; // -1 = All

        if (rankable && RankFilterCombo.SelectedItem is string sel && sel != "All")
        {
            // Parse rank number from display string ("0 (Unranked)" → 0, "5" → 5)
            var numPart = sel.Split(' ')[0];
            int.TryParse(numPart, out selectedRank);
        }

        var filtered = rankable && selectedRank >= 0
            ? _allOrders.Where(o => o.ModRank == selectedRank).ToList()
            : _allOrders;

        // When a specific rank is filtered, show all statuses so the user sees the full market.
        // When showing all ranks (no filter), prioritize ingame → online → all.
        List<MarketOrder> pool;
        if (rankable && selectedRank >= 0)
        {
            pool = filtered;
        }
        else
        {
            var ingame = filtered.Where(o => o.UserStatus == "ingame").ToList();
            pool = ingame.Count > 0 ? ingame : filtered.Where(o => o.UserStatus is "ingame" or "online").ToList();
        }

        // For non-rankable items, hide the rank column to save space
        var showRank = rankable;
        SellRankCol.Visibility = showRank ? Visibility.Visible : Visibility.Collapsed;
        BuyRankCol.Visibility = showRank ? Visibility.Visible : Visibility.Collapsed;

        var sellOrders = pool.Where(o => o.OrderType == "sell").ToList();
        var buyOrders = pool.Where(o => o.OrderType == "buy").ToList();

        // Sort: rankable items sort by rank first, then by price
        if (rankable)
        {
            SellGrid.ItemsSource = sellOrders
                .OrderBy(o => o.ModRank ?? 0)
                .ThenBy(o => o.Platinum)
                .Take(50).ToList();
            BuyGrid.ItemsSource = buyOrders
                .OrderBy(o => o.ModRank ?? 0)
                .ThenByDescending(o => o.Platinum)
                .Take(50).ToList();
        }
        else
        {
            SellGrid.ItemsSource = sellOrders.OrderBy(o => o.Platinum).Take(50).ToList();
            BuyGrid.ItemsSource = buyOrders.OrderByDescending(o => o.Platinum).Take(50).ToList();
        }

        // Update header to show rank context and order counts
        var rankHint = rankable && selectedRank >= 0 ? $" [RANK {selectedRank}]" : "";
        SellHeader.Text = $"SELL ORDERS ({sellOrders.Count}){rankHint}";
        BuyHeader.Text = $"BUY ORDERS ({buyOrders.Count}){rankHint}";
    }

    private void OnChartSizeChanged(object sender, SizeChangedEventArgs e) => RenderChart();

    private void RenderChart()
    {
        ChartCanvas.Children.Clear();
        if (_chartData.Count < 2) { ChartInfo.Text = _chartData.Count == 0 ? "No trade statistics." : ""; return; }

        double w = ChartCanvas.ActualWidth, h = ChartCanvas.ActualHeight;
        if (w < 20 || h < 20) return;

        var min = _chartData.Min(p => p.Median);
        var max = _chartData.Max(p => p.Median);
        var range = Math.Max(max - min, 0.001);

        var line = new Polyline
        {
            Stroke = (Brush)FindResource("Brush.Accent"),
            StrokeThickness = 1.6,
            Effect = (System.Windows.Media.Effects.Effect)FindResource("Fx.GlowSoft"),
        };
        for (int i = 0; i < _chartData.Count; i++)
        {
            double x = i / (double)(_chartData.Count - 1) * (w - 8) + 4;
            double y = h - 8 - (_chartData[i].Median - min) / range * (h - 16);
            line.Points.Add(new Point(x, y));
        }
        ChartCanvas.Children.Add(line);

        var last = _chartData[^1];
        var volume = _chartData.Sum(p => p.Volume);
        var rankNote = (_currentDetails?.IsRankable == true || (_allOrders?.Any(o => o.ModRank.HasValue) == true))
            ? " · ⚠ mixed ranks (aggregate)" : "";
        ChartInfo.Text = $"now ~{last.Median:0}p · min {min:0}p · max {max:0}p · {volume:N0} sold in 90d{rankNote}";
    }

    // ---- ledger -------------------------------------------------------------

    private void RefreshLedger()
    {
        var market = App.Services.Market;
        TradeGrid.ItemsSource = market.GetTrades();
        var (pIn, pOut, net, _) = market.GetLedgerStats();
        PlatInText.Text = $"+{pIn:N0}";
        PlatOutText.Text = $"-{pOut:N0}";
        PlatNetText.Text = net.ToString("N0");
        PlatNetText.Foreground = (Brush)FindResource(net >= 0 ? "Brush.Accent" : "Brush.Danger");
    }

    private void OnAddTrade(object sender, RoutedEventArgs e)
    {
        var item = TradeItem.Text.Trim();
        if (item.Length == 0) { TradeStatus.Text = "Enter the item name."; return; }
        if (!int.TryParse(TradeQty.Text, out var qty) || qty <= 0) { TradeStatus.Text = "Bad quantity."; return; }
        if (!int.TryParse(TradePlat.Text, out var plat) || plat < 0) { TradeStatus.Text = "Bad platinum amount."; return; }

        App.Services.Market.AddTrade(new TradeEntry
        {
            Date = DateTimeOffset.Now,
            ItemName = item,
            Quantity = qty,
            Platinum = plat,
            IsSale = TradeDirection.SelectedIndex == 0,
        });
        TradeItem.Text = TradePlat.Text = "";
        TradeQty.Text = "1";
        TradeStatus.Text = "Logged.";
        RefreshLedger();
    }

    private void OnDeleteTrade(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: long id })
        {
            App.Services.Market.DeleteTrade(id);
            RefreshLedger();
        }
    }
}
