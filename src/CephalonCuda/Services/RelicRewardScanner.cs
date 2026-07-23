using System.Drawing;
using CephalonCuda.Models;

namespace CephalonCuda.Services;

/// <summary>
/// Detects and values the relic reward-choice screen: captures a horizontal band where the 2–4
/// reward cards sit, clusters OCR words into card columns, fuzzy-matches each column against the
/// full set of possible relic rewards, and joins live warframe.market prices.
/// </summary>
public sealed class RelicRewardScanner(
    GameWindowTracker tracker,
    ScreenCaptureService capture,
    OcrService ocr,
    MarketService market,
    RelicDataService relicData,
    ContextAggregator context)
{
    private HashSet<string>? _candidates; // every item that can drop from a relic

    public event Action<List<RewardHit>>? ScanCompleted;
    public event Action<string>? ScanFailed;

    public async Task<List<RewardHit>> ScanAsync(CancellationToken ct = default)
    {
        try
        {
            var hits = await ScanCoreAsync(ct);
            context.LastRewardScan = hits;
            ScanCompleted?.Invoke(hits);
            return hits;
        }
        catch (Exception ex)
        {
            ScanFailed?.Invoke(ex.Message);
            return [];
        }
    }

    private async Task<List<RewardHit>> ScanCoreAsync(CancellationToken ct)
    {
        var bounds = tracker.CaptureBounds;
        // Two-pass scan: tight band first (works for most setups), then a wider fallback
        // for resolutions/UI scales where the reward cards sit at different vertical positions.
        // Horizontal band stays tight (10%-90%) to avoid noise from surrounding UI.
        Rectangle[] bands =
        [
            // Pass 1: tight band — works for 1080p and most standard HUD scales
            new(
                bounds.X + (int)(bounds.Width * 0.10),
                bounds.Y + (int)(bounds.Height * 0.26),
                (int)(bounds.Width * 0.80),
                (int)(bounds.Height * 0.30)),
            // Pass 2: wider vertical band — fallback for 1440p, 4K, or non-standard HUD scales
            new(
                bounds.X + (int)(bounds.Width * 0.10),
                bounds.Y + (int)(bounds.Height * 0.18),
                (int)(bounds.Width * 0.80),
                (int)(bounds.Height * 0.46)),
        ];

        await market.GetPriceSnapshotAsync(ct: ct);
        var candidates = await GetCandidatesAsync(ct);

        foreach (var band in bands)
        {
            var frame = capture.CaptureRegion(band)
                ?? throw new InvalidOperationException("Screen capture failed (DXGI and GDI). Is the game in exclusive fullscreen? Use Borderless.");

            var words = await ocr.RecognizeWordsAsync(frame);
            if (words.Count == 0) continue;

            var hits = new List<RewardHit>();
            foreach (var columnText in ClusterIntoColumns(words, frame.Width))
            {
                var (name, score) = BestMatch(columnText, candidates);
                if (score < 0.45) continue;
                var snap = market.LookupPrice(name);
                hits.Add(new RewardHit
                {
                    RawText = columnText,
                    MatchedName = name,
                    Score = score,
                    Plat = snap?.WaPrice ?? 0,
                    Ducats = snap?.Ducats ?? 0,
                });
            }

            // If we found at least 2 rewards, this band worked — return results.
            // If 0 rewards, try the next (wider) band.
            if (hits.Count >= 2 || band == bands[^1])
            {
                if (hits.Count > 0)
                {
                    var best = hits.MaxBy(h => h.Plat);
                    if (best is not null && best.Plat > 0) best.IsBest = true;
                }
                return hits;
            }
        }

        return [];
    }

    /// <summary>Group words into up to 4 card columns using horizontal gaps.</summary>
    private static List<string> ClusterIntoColumns(List<OcrWord> words, int frameWidth)
    {
        var minGap = frameWidth * 0.035;
        var clusters = new List<List<OcrWord>>();
        foreach (var word in words.OrderBy(w => w.X))
        {
            var target = clusters.FirstOrDefault(c =>
                word.X <= c.Max(w => w.X + w.W) + minGap && word.X + word.W >= c.Min(w => w.X) - minGap);
            if (target is null) clusters.Add([word]);
            else target.Add(word);
        }
        return clusters
            .Where(c => c.Sum(w => w.Text.Length) >= 4)
            .OrderBy(c => c.Min(w => w.X))
            .Take(4)
            .Select(c => string.Join(" ", c.OrderBy(w => w.CenterY).ThenBy(w => w.X).Select(w => w.Text)))
            .ToList();
    }

    private async Task<HashSet<string>> GetCandidatesAsync(CancellationToken ct)
    {
        if (_candidates is not null) return _candidates;
        var relics = await relicData.GetRelicsAsync(ct);
        _candidates = relics.SelectMany(r => r.Rewards).Select(r => r.ItemName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _candidates.Add("Forma Blueprint");
        return _candidates;
    }

    private static (string Name, double Score) BestMatch(string raw, HashSet<string> candidates)
    {
        var normRaw = Normalize(raw);
        string bestName = "";
        double bestScore = 0;
        foreach (var candidate in candidates)
        {
            var normCand = Normalize(candidate);
            var maxLen = Math.Max(normRaw.Length, normCand.Length);
            if (maxLen == 0) continue;
            // Levenshtein distance is never less than the length difference, so similarity can't
            // exceed this bound — skip the O(n*m) DP pass for candidates that can't win anyway.
            var upperBound = 1.0 - (double)Math.Abs(normRaw.Length - normCand.Length) / maxLen;
            if (upperBound <= bestScore) continue;

            var score = Similarity(normRaw, normCand);
            if (score > bestScore) { bestScore = score; bestName = candidate; }
        }
        return (bestName, bestScore);
    }

    private static string Normalize(string s) =>
        new([.. s.ToUpperInvariant().Where(char.IsLetterOrDigit)]);

    /// <summary>Normalized Levenshtein similarity in [0,1].</summary>
    private static double Similarity(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            }
        return 1.0 - (double)d[a.Length, b.Length] / Math.Max(a.Length, b.Length);
    }
}
