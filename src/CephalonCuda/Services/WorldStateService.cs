using System.Net.Http;
using System.Text.Json;
using CephalonCuda.Models;

namespace CephalonCuda.Services;

/// <summary>Polls the public Warframe world state (api.warframestat.us) every 60s.</summary>
public sealed class WorldStateService : IDisposable
{
    private const string Url = "https://api.warframestat.us/pc/?language=en";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private CancellationTokenSource? _cts;

    public WorldState? Current { get; private set; }
    public string? LastError { get; private set; }
    public event Action<WorldState>? Updated;
    public event Action<string>? Failed;

    public WorldStateService(HttpClient http) => _http = http;

    public void Start()
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => LoopAsync(_cts.Token));
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await RefreshAsync(ct);
            try { await Task.Delay(TimeSpan.FromSeconds(60), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        try
        {
            var json = await _http.GetStringAsync(Url, ct);
            var state = JsonSerializer.Deserialize<WorldState>(json, JsonOpts);
            if (state is null) return;
            state.FetchedAt = DateTimeOffset.UtcNow;
            Current = state;
            LastError = null;
            Updated?.Invoke(state);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastError = ex.Message;
            Failed?.Invoke(ex.Message);
        }
    }

    /// <summary>Compressed summary for AI context injection.</summary>
    public object? Summarize()
    {
        var s = Current;
        if (s is null) return null;
        return new
        {
            fissures = s.Fissures.Where(f => !f.Expired).GroupBy(f => f.Tier)
                .ToDictionary(g => g.Key ?? "?", g => g.Count()),
            steelPathFissures = s.Fissures.Count(f => f.IsHard && !f.Expired),
            voidStorms = s.Fissures.Count(f => f.IsStorm && !f.Expired),
            sortie = s.Sortie is null ? null : $"{s.Sortie.Boss} ({s.Sortie.Faction}), ends in {s.Sortie.TimeLeft}",
            archonHunt = s.ArchonHunt is null ? null : $"{s.ArchonHunt.Boss}, ends in {s.ArchonHunt.TimeLeft}",
            baro = s.VoidTrader?.StatusLine,
            cycles = new
            {
                cetus = s.CetusCycle?.State,
                vallis = s.VallisCycle?.State,
                deimos = s.CambionCycle?.State,
                duviri = s.DuviriCycle?.State,
            },
            arbitration = s.Arbitration?.Node,
            invasions = s.Invasions.Count(i => !i.Completed),
            alerts = s.Alerts.Take(8).Select(a => new
            {
                node = a.Mission?.Node,
                type = a.Mission?.Type,
                reward = a.Mission?.Reward?.AsString ?? a.Mission?.Reward?.ItemString,
                a.Eta,
            }),
            nightwave = s.Nightwave is null ? null : new
            {
                s.Nightwave.Tag,
                s.Nightwave.Season,
                challenges = s.Nightwave.ActiveChallenges.Take(12).Select(c => new
                {
                    c.Title, c.Desc, c.Reputation, c.IsDaily, c.IsElite,
                }),
            },
            recentOfficialNews = s.News
                .OrderByDescending(n => n.Date)
                .Take(10)
                .Select(n => new { n.Message, n.Link, n.Date, n.Update, n.PrimeAccess }),
            fetchedAt = s.FetchedAt.ToString("O"),
        };
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
