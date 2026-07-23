using System.IO;
using System.Net.Http;
using System.Text.Json;
using CephalonCuda.Models;

namespace CephalonCuda.Services;

/// <summary>
/// Relic drop tables from the public WFCD data export (drops.warframestat.us).
/// Cached on disk for 24h; each relic appears once per refinement state.
/// </summary>
public sealed class RelicDataService(HttpClient http)
{
    private const string Url = "https://drops.warframestat.us/data/relics.json";
    private static string CachePath => Path.Combine(Db.AppDataDir, "relics.json");

    private List<RelicInfo>? _all;

    public async Task<List<RelicInfo>> GetRelicsAsync(CancellationToken ct = default)
    {
        if (_all is not null) return _all;

        string? json = null;
        var cacheFresh = File.Exists(CachePath) &&
                         DateTime.UtcNow - File.GetLastWriteTimeUtc(CachePath) < TimeSpan.FromHours(24);
        if (cacheFresh)
            json = await File.ReadAllTextAsync(CachePath, ct);
        else
        {
            try
            {
                json = await http.GetStringAsync(Url, ct);
                await File.WriteAllTextAsync(CachePath, json, ct);
            }
            catch when (File.Exists(CachePath))
            {
                json = await File.ReadAllTextAsync(CachePath, ct); // offline fallback to stale cache
            }
        }
        if (json is null) return _all = [];

        var list = new List<RelicInfo>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("relics", out var relics))
        {
            foreach (var el in relics.EnumerateArray())
            {
                // Tolerant parse: the export occasionally contains entries with missing keys.
                if (!el.TryGetProperty("tier", out var tier) ||
                    !el.TryGetProperty("relicName", out var name) ||
                    !el.TryGetProperty("state", out var state))
                    continue;

                var relic = new RelicInfo
                {
                    Tier = tier.GetString() ?? "",
                    Name = name.GetString() ?? "",
                    State = state.GetString() ?? "",
                };
                if (el.TryGetProperty("rewards", out var rewards) && rewards.ValueKind == JsonValueKind.Array)
                    foreach (var r in rewards.EnumerateArray())
                    {
                        if (!r.TryGetProperty("itemName", out var item)) continue;
                        relic.Rewards.Add(new RelicReward
                        {
                            ItemName = item.GetString() ?? "",
                            Rarity = r.TryGetProperty("rarity", out var ra) ? ra.GetString() ?? "" : "",
                            Chance = r.TryGetProperty("chance", out var ch) && ch.ValueKind == JsonValueKind.Number ? ch.GetDouble() : 0,
                        });
                    }
                list.Add(relic);
            }
        }
        return _all = list;
    }

    /// <summary>Relics for one refinement state with plat/ducat EV computed from the price snapshot.</summary>
    public async Task<List<RelicInfo>> GetValuedRelicsAsync(
        string state, Func<string, PriceSnapshot?> priceLookup, CancellationToken ct = default)
    {
        var all = await GetRelicsAsync(ct);
        var result = new List<RelicInfo>();
        foreach (var relic in all.Where(r => r.State.Equals(state, StringComparison.OrdinalIgnoreCase)))
        {
            double ev = 0, best = 0;
            string? bestItem = null;
            foreach (var reward in relic.Rewards)
            {
                var snap = priceLookup(reward.ItemName);
                reward.Plat = snap?.WaPrice ?? 0;
                reward.Ducats = snap?.Ducats ?? 0;
                ev += reward.Chance / 100.0 * reward.Plat;
                if (reward.Plat > best) { best = reward.Plat; bestItem = reward.ItemName; }
            }
            relic.ExpectedPlat = ev;
            relic.BestPlat = best;
            relic.BestItem = bestItem;
            result.Add(relic);
        }
        return result.OrderByDescending(r => r.ExpectedPlat).ToList();
    }
}
