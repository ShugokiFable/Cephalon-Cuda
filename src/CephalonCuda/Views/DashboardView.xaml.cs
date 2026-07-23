using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CephalonCuda.Models;

namespace CephalonCuda.Views;

public partial class DashboardView : UserControl
{
    private Action<WorldState>? _handler;
    private readonly DispatcherTimer _tick;

    public sealed record FissureRow(string Tier, string Mission, string Node, string Enemy, string Mode, string Eta);
    public sealed record InvasionRow(string Title, double Completion, string Rewards);

    public DashboardView()
    {
        InitializeComponent();

        // Tick the countdowns every second without re-fetching the world state (that stays on its 60s loop).
        _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += (_, _) => { if (App.Services.WorldState.Current is { } ws) RenderTimers(ws); };

        Loaded += (_, _) =>
        {
            if (_handler is null)
            {
                _handler = ws => Dispatcher.Invoke(() => Render(ws));
                App.Services.WorldState.Updated += _handler;
            }
            if (App.Services.WorldState.Current is { } ws) Render(ws);
            _tick.Start();
        };
        Unloaded += (_, _) =>
        {
            _tick.Stop();
            if (_handler is not null)
            {
                App.Services.WorldState.Updated -= _handler;
                _handler = null;
            }
        };
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) =>
        await App.Services.WorldState.RefreshAsync();

    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        if (App.Services.WorldState.Current is { } ws) RenderFissures(ws);
    }

    private void Render(WorldState ws)
    {
        RenderFissures(ws);
        RenderTimers(ws);

        CyclesText.Text =
            $"Cetus   {Fmt(ws.CetusCycle)}\n" +
            $"Vallis  {Fmt(ws.VallisCycle)}\n" +
            $"Deimos  {Fmt(ws.CambionCycle)}\n" +
            $"Duviri  {Fmt(ws.DuviriCycle)}\n" +
            $"Earth   {Fmt(ws.EarthCycle)}";

        ArbitrationText.Text = ws.Arbitration is { Node.Length: > 0 } arb
            ? $"Arbitration: {arb.Type} @ {arb.Node} ({arb.Enemy})"
            : "Arbitration: —";
        AlertsText.Text = ws.Alerts.Count > 0
            ? string.Join("\n", ws.Alerts.Take(5).Select(al =>
                $"Alert: {al.Mission?.Type} @ {al.Mission?.Node} → {al.Mission?.Reward?.AsString} ({al.Eta})"))
            : "Alerts: none";

        InvasionList.ItemsSource = ws.Invasions.Where(i => !i.Completed).Take(8)
            .Select(i => new InvasionRow(
                $"{i.Node} — {i.Desc}",
                Math.Clamp(i.Completion, 0, 100),
                $"{i.Attacker?.Reward?.AsString ?? "—"}  vs  {i.Defender?.Reward?.AsString ?? "—"}"))
            .ToList();

        NewsList.ItemsSource = ws.News
            .Where(n => !string.IsNullOrWhiteSpace(n.Message))
            .TakeLast(6).Reverse()
            .Select(n => $"• {n.Message}")
            .ToList();
    }

    /// <summary>Just the countdown-bearing panels; called every second so timers tick without a refetch.</summary>
    private void RenderTimers(WorldState ws)
    {
        SortieText.Text = ws.Sortie is { } s
            ? $"{s.Boss} ({s.Faction}) — ends in {s.TimeLeft}\n" + string.Join("\n",
                s.Variants.Select((v, i) => $"  {i + 1}. {v.MissionType} @ {v.Node} — {v.Modifier}"))
            : "No sortie data.";

        ArchonText.Text = ws.ArchonHunt is { } a
            ? $"{a.Boss} ({a.Faction}) — ends in {a.TimeLeft}\n" + string.Join("\n",
                a.Missions.Select((m, i) => $"  {i + 1}. {m.Type} @ {m.Node}"))
            : "No archon hunt data.";

        if (ws.VoidTrader is { } baro)
        {
            BaroText.Text = baro.StatusLine;
            BaroInventory.ItemsSource = baro.IsHere && baro.Inventory.Count > 0
                ? baro.Inventory.Select(i => $"{i.Item} — {i.Ducats}d + {i.Credits:N0}cr").ToList()
                : null;
        }
        else BaroText.Text = "No trader data.";
    }

    private void RenderFissures(WorldState ws)
    {
        var fissures = ws.Fissures.Where(f => !f.Expired);
        if (SteelPathOnly.IsChecked == true) fissures = fissures.Where(f => f.IsHard);
        if (StormsOnly.IsChecked == true) fissures = fissures.Where(f => f.IsStorm);

        FissureGrid.ItemsSource = fissures
            .OrderBy(f => f.TierNum)
            .Select(f => new FissureRow(
                f.Tier ?? "?", f.MissionType ?? "?", f.Node ?? "?", f.Enemy ?? "?",
                f.IsStorm ? "Storm" : f.IsHard ? "SP" : "Normal",
                f.Eta ?? "?"))
            .ToList();
    }

    private static string Fmt(WorldCycle? c) =>
        c is null ? "—" : $"{c.State?.ToUpperInvariant(),-6} ({c.TimeLeft} left)";
}
