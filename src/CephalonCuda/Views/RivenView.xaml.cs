using System.Windows;
using System.Windows.Controls;
using CephalonCuda.Models;

namespace CephalonCuda.Views;

public partial class RivenView : UserControl
{
    private List<(string UrlName, string ItemName)> _weapons = [];
    private List<RivenAuctionInfo> _comparables = [];

    public RivenView()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (_weapons.Count > 0) return;
            try
            {
                _weapons = await App.Services.Market.GetRivenWeaponsAsync();
                WeaponBox.ItemsSource = _weapons.Select(w => w.ItemName).ToList();
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Could not load riven weapon list: {ex.Message}";
            }
        };
    }

    private (string UrlName, string ItemName)? SelectedWeapon =>
        WeaponBox.SelectedIndex >= 0 && WeaponBox.SelectedIndex < _weapons.Count
            ? _weapons[WeaponBox.SelectedIndex] : null;

    private async void OnFetchComparables(object sender, RoutedEventArgs e)
    {
        if (SelectedWeapon is not { } weapon) { StatusText.Text = "Pick a weapon first."; return; }
        StatusText.Text = $"Fetching auctions for {weapon.ItemName}…";
        try
        {
            _comparables = await App.Services.Market.SearchRivenAuctionsAsync(weapon.UrlName);
            AuctionGrid.ItemsSource = _comparables;

            var buyouts = _comparables.Where(a => a.BuyoutPrice is > 0).Select(a => (double)a.BuyoutPrice!).OrderBy(x => x).ToList();
            AuctionSummary.Text = buyouts.Count > 0
                ? $"{_comparables.Count} auctions · buyouts: min {buyouts[0]:0}p · median {buyouts[buyouts.Count / 2]:0}p"
                : $"{_comparables.Count} auctions (no direct buyouts).";
            StatusText.Text = "Comparables loaded.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Fetch failed: {ex.Message}";
        }
    }

    private async void OnAiValuation(object sender, RoutedEventArgs e)
    {
        if (SelectedWeapon is not { } weapon) { StatusText.Text = "Pick a weapon first."; return; }
        if (StatsBox.Text.Trim().Length == 0) { StatusText.Text = "Enter the riven's stats."; return; }
        if (!App.Services.Settings.HasApiKey) { StatusText.Text = "Set an OpenRouter API key in Settings first."; return; }

        StatusText.Text = "Consulting the void…";
        VerdictBox.Text = "";
        try
        {
            var comparableText = _comparables.Count > 0
                ? string.Join("\n", _comparables.Take(12).Select(a =>
                    $"- {(a.BuyoutPrice is { } b ? $"{b}p buyout" : $"{a.StartingPrice}p start")} · rr{a.Rerolls} · {a.AttributesDisplay}"))
                : "(none fetched — judge from general riven knowledge and say prices are approximate)";

            var prompt = $"""
                Evaluate this {weapon.ItemName} riven:
                Stats:
                {StatsBox.Text.Trim()}
                Rerolls: {RerollsBox.Text}, Rank: {RankBox.Text}, Polarity: {(PolarityBox.SelectedItem as ComboBoxItem)?.Content}

                Live comparable auctions on warframe.market right now:
                {comparableText}

                Give: 1) stat grade for this weapon's meta builds, 2) estimated sale price range in platinum
                with reasoning against the comparables, 3) reroll or keep recommendation.
                """;

            var messages = new List<ChatMsg>
            {
                App.Services.Context.BuildSystemMessage(prompt),
                new() { Role = "user", Content = prompt },
            };
            var reply = await App.Services.OpenRouter.CompleteAsync(
                messages, App.Services.Settings.ChatModel, cacheTtl: TimeSpan.FromMinutes(30));
            VerdictBox.Text = reply;
            StatusText.Text = "Valuation complete.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"AI call failed: {ex.Message}";
        }
    }
}
