using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using CephalonCuda.Models;
using Microsoft.Data.Sqlite;

namespace CephalonCuda.Services;

/// <summary>
/// warframe.market REST client with SQLite caching and polite rate limiting (~3 req/s).
/// Uses documented API v2 routes for catalog, item metadata, orders and riven manifests.
/// Legacy v1 bulk economics, statistics and auction routes are optional enrichments with
/// local-cache and v2 metadata fallbacks because the public v2 rollout is still incomplete.
/// </summary>
public sealed class MarketService(HttpClient http, Db db)
{
    private const string Base = "https://api.warframe.market";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _lastCall = DateTimeOffset.MinValue;

    private Dictionary<string, PriceSnapshot>? _snapshotByName; // in-memory index, name lowercased
    private List<(string UrlName, string ItemName)>? _rivenWeapons;
    private List<MarketItem>? _catalogByLongestName;

    /// <summary>Last synced warframe.market account state, shared with the advisor context.</summary>
    public List<UserMarketOrder> MyOrders { get; private set; } = [];
    public MarketMe? Me { get; private set; }
    public DateTimeOffset? AccountSyncedAt { get; private set; }
    public string? LastSnapshotWarning { get; private set; }

    private async Task<JsonDocument> GetJsonAsync(
        string path,
        CancellationToken ct = default,
        string? jwt = null,
        TimeSpan? cacheTtl = null,
        bool allowStale = true)
    {
        string? stalePayload = null;
        string? cachedEtag = null;
        string? cachedLastModified = null;
        if (jwt is null && cacheTtl is not null)
        {
            var cached = db.Query(
                "SELECT payload,expires_at,etag,last_modified FROM api_cache WHERE cache_key=@k LIMIT 1",
                r => (Payload: r.GetString(0), ExpiresAt: DateTimeOffset.Parse(r.GetString(1)),
                    Etag: r.IsDBNull(2) ? null : r.GetString(2),
                    LastModified: r.IsDBNull(3) ? null : r.GetString(3)),
                ("@k", path)).FirstOrDefault();
            stalePayload = cached.Payload;
            cachedEtag = cached.Etag;
            cachedLastModified = cached.LastModified;
            if (cached.Payload is not null && cached.ExpiresAt > DateTimeOffset.UtcNow)
                return JsonDocument.Parse(cached.Payload);
        }

        Exception? lastError = null;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            await _gate.WaitAsync(ct);
            try
            {
                var wait = _lastCall + TimeSpan.FromMilliseconds(350) - DateTimeOffset.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);

                using var req = new HttpRequestMessage(HttpMethod.Get, Base + path);
                req.Headers.TryAddWithoutValidation("Accept", "application/json");
                req.Headers.TryAddWithoutValidation("Language", "en");
                req.Headers.TryAddWithoutValidation("Platform", "pc");
                req.Headers.TryAddWithoutValidation("Crossplay", "true");
                if (!string.IsNullOrEmpty(jwt))
                    req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {jwt}");
                if (jwt is null && stalePayload is not null)
                {
                    if (!string.IsNullOrWhiteSpace(cachedEtag))
                        req.Headers.TryAddWithoutValidation("If-None-Match", cachedEtag);
                    if (DateTimeOffset.TryParse(cachedLastModified, out var modified))
                        req.Headers.TryAddWithoutValidation("If-Modified-Since", modified.ToString("R"));
                }

                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                _lastCall = DateTimeOffset.UtcNow;
                if (resp.StatusCode == HttpStatusCode.NotModified && stalePayload is not null && cacheTtl is not null)
                {
                    var refreshed = DateTimeOffset.UtcNow;
                    db.Exec("UPDATE api_cache SET fetched_at=@f,expires_at=@e WHERE cache_key=@k",
                        ("@f", refreshed.ToString("O")), ("@e", refreshed.Add(cacheTtl.Value).ToString("O")), ("@k", path));
                    return JsonDocument.Parse(stalePayload);
                }
                if (resp.IsSuccessStatusCode)
                {
                    var text = await resp.Content.ReadAsStringAsync(ct);
                    if (jwt is null && cacheTtl is not null)
                    {
                        var fetched = DateTimeOffset.UtcNow;
                        db.Exec("""
                            INSERT INTO api_cache(cache_key,payload,fetched_at,expires_at,etag,last_modified)
                            VALUES(@k,@p,@f,@e,@tag,@lm)
                            ON CONFLICT(cache_key) DO UPDATE SET payload=@p,fetched_at=@f,expires_at=@e,etag=@tag,last_modified=@lm
                            """,
                            ("@k", path), ("@p", text), ("@f", fetched.ToString("O")),
                            ("@e", fetched.Add(cacheTtl.Value).ToString("O")),
                            ("@tag", resp.Headers.ETag?.Tag),
                            ("@lm", resp.Content.Headers.LastModified?.ToString("O")));
                    }
                    return JsonDocument.Parse(text);
                }

                var retryable = resp.StatusCode == HttpStatusCode.TooManyRequests || (int)resp.StatusCode >= 500;
                var detail = await resp.Content.ReadAsStringAsync(ct);
                lastError = new HttpRequestException(
                    $"warframe.market {(int)resp.StatusCode} {resp.ReasonPhrase}: {detail[..Math.Min(detail.Length, 240)]}",
                    null, resp.StatusCode);
                if (!retryable) break;

                var retryAfter = resp.Headers.RetryAfter?.Delta
                                 ?? (resp.Headers.RetryAfter?.Date is { } d ? d - DateTimeOffset.UtcNow : null);
                var backoff = retryAfter is { } ra && ra > TimeSpan.Zero
                    ? ra
                    : TimeSpan.FromMilliseconds(450 * Math.Pow(2, attempt));
                await Task.Delay(backoff, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                lastError = ex;
                if (attempt < 3) await Task.Delay(TimeSpan.FromMilliseconds(450 * Math.Pow(2, attempt)), ct);
            }
            finally
            {
                _gate.Release();
            }
        }

        if (allowStale && stalePayload is not null)
            return JsonDocument.Parse(stalePayload);
        throw lastError ?? new HttpRequestException("warframe.market request failed.");
    }

    // ---- item catalog -----------------------------------------------------

    /// <summary>Ensure the ~4000-item catalog is cached locally. Refreshes weekly or on demand.</summary>
    public async Task<int> EnsureItemCatalogAsync(bool force = false, CancellationToken ct = default)
    {
        var count = db.Scalar<long>("SELECT COUNT(*) FROM market_items");
        var age = db.Scalar<string>("SELECT value FROM settings WHERE key='items_fetched_at'");
        var fresh = DateTimeOffset.TryParse(age, out var at) && DateTimeOffset.UtcNow - at < TimeSpan.FromDays(7);
        if (!force && count > 0 && fresh) return (int)count;

        using var doc = await GetJsonAsync("/v2/items", ct, cacheTtl: force ? null : TimeSpan.FromDays(7));
        var items = doc.RootElement.GetProperty("data");
        var parsed = new List<(string Id, string Name, string Slug, string? Thumb, int Ducats)>();
        foreach (var el in items.EnumerateArray())
        {
            if (!el.TryGetProperty("id", out var id) || !el.TryGetProperty("slug", out var slug)) continue;
            var en = el.TryGetProperty("i18n", out var i18n) && i18n.TryGetProperty("en", out var e) ? e : default;
            var name = en.ValueKind == JsonValueKind.Object && en.TryGetProperty("name", out var nm) ? nm.GetString() : null;
            var itemId = id.GetString();
            var itemSlug = slug.GetString();
            if (string.IsNullOrWhiteSpace(itemId) || string.IsNullOrWhiteSpace(itemSlug) || string.IsNullOrWhiteSpace(name)) continue;
            var ducats = el.TryGetProperty("ducats", out var ducatValue) && ducatValue.ValueKind == JsonValueKind.Number
                ? ducatValue.GetInt32()
                : 0;
            parsed.Add((itemId, name, itemSlug,
                en.TryGetProperty("thumb", out var th) ? th.GetString() : null, ducats));
        }
        if (parsed.Count < 500)
            throw new InvalidDataException($"warframe.market returned only {parsed.Count} catalog items; keeping the current cache.");

        db.Bulk((c, tx) =>
        {
            using (var clear = c.CreateCommand())
            {
                clear.Transaction = tx;
                clear.CommandText = "DELETE FROM market_items";
                clear.ExecuteNonQuery();
            }
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO market_items(id,item_name,url_name,thumb) VALUES(@i,@n,@u,@t)";
            var pi = cmd.Parameters.Add("@i", SqliteType.Text);
            var pn = cmd.Parameters.Add("@n", SqliteType.Text);
            var pu = cmd.Parameters.Add("@u", SqliteType.Text);
            var pt = cmd.Parameters.Add("@t", SqliteType.Text);
            foreach (var row in parsed)
            {
                pi.Value = row.Id;
                pn.Value = row.Name;
                pu.Value = row.Slug;
                pt.Value = row.Thumb as object ?? DBNull.Value;
                cmd.ExecuteNonQuery();
            }

            using var seed = c.CreateCommand();
            seed.Transaction = tx;
            seed.CommandText = """
                INSERT INTO price_snapshot(item_id,item_name,url_name,ducats,wa_price,median,fetched_at)
                VALUES(@i,@n,@u,@d,0,0,@f)
                ON CONFLICT(item_id) DO UPDATE SET
                    item_name=excluded.item_name,
                    url_name=excluded.url_name,
                    ducats=excluded.ducats
                """;
            var si = seed.Parameters.Add("@i", SqliteType.Text);
            var sn = seed.Parameters.Add("@n", SqliteType.Text);
            var su = seed.Parameters.Add("@u", SqliteType.Text);
            var sd = seed.Parameters.Add("@d", SqliteType.Integer);
            var sf = seed.Parameters.Add("@f", SqliteType.Text);
            var seededAt = DateTimeOffset.UtcNow.ToString("O");
            foreach (var row in parsed)
            {
                si.Value = row.Id;
                sn.Value = row.Name;
                su.Value = row.Slug;
                sd.Value = row.Ducats;
                sf.Value = seededAt;
                seed.ExecuteNonQuery();
            }
        });
        db.Exec("INSERT INTO settings(key,value) VALUES('items_fetched_at',@v) ON CONFLICT(key) DO UPDATE SET value=@v",
            ("@v", DateTimeOffset.UtcNow.ToString("O")));
        _catalogByLongestName = null;
        return parsed.Count;
    }

    public List<MarketItem> SearchItems(string query, int limit = 25) =>
        db.Query("SELECT id,item_name,url_name,thumb FROM market_items WHERE item_name LIKE @q ORDER BY item_name LIMIT @l",
            r => new MarketItem { Id = r.GetString(0), ItemName = r.GetString(1), UrlName = r.GetString(2), Thumb = r.IsDBNull(3) ? null : r.GetString(3) },
            ("@q", $"%{query}%"), ("@l", limit));

    /// <summary>
    /// Resolve an item explicitly named in natural-language chat. Longest names win so
    /// "Arcane Energize" is chosen before a shorter overlapping catalog label.
    /// </summary>
    public MarketItem? FindMentionedItem(string text)
    {
        var query = NormalizeName(text);
        if (query.Length < 2) return null;
        _catalogByLongestName ??= db.Query(
                "SELECT id,item_name,url_name,thumb FROM market_items ORDER BY length(item_name) DESC",
                r => new MarketItem
                {
                    Id = r.GetString(0), ItemName = r.GetString(1), UrlName = r.GetString(2),
                    Thumb = r.IsDBNull(3) ? null : r.GetString(3),
                })
            .Where(i => NormalizeName(i.ItemName).Length >= 4)
            .ToList();

        return _catalogByLongestName.FirstOrDefault(item =>
        {
            var name = NormalizeName(item.ItemName);
            return query.Equals(name, StringComparison.Ordinal) ||
                   query.StartsWith(name + " ", StringComparison.Ordinal) ||
                   query.EndsWith(" " + name, StringComparison.Ordinal) ||
                   query.Contains(" " + name + " ", StringComparison.Ordinal);
        });
    }

    private static string NormalizeName(string value)
    {
        var b = new System.Text.StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var ch in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                if (pendingSpace && b.Length > 0) b.Append(' ');
                b.Append(ch);
                pendingSpace = false;
            }
            else pendingSpace = true;
        }
        return b.ToString().Trim();
    }

    // ---- bulk price snapshot ----------------------------------------------

    /// <summary>Load cached bulk prices when available, always retaining v2 item and Ducat metadata as a fallback.</summary>
    public async Task<Dictionary<string, PriceSnapshot>> GetPriceSnapshotAsync(bool force = false, CancellationToken ct = default)
    {
        var rowCount = db.Scalar<long>("SELECT COUNT(*) FROM price_snapshot");
        var pricedCount = db.Scalar<long>("SELECT COUNT(*) FROM price_snapshot WHERE wa_price > 0 OR median > 0");
        var newest = db.Scalar<string>("SELECT MAX(fetched_at) FROM price_snapshot WHERE wa_price > 0 OR median > 0");
        var fresh = pricedCount > 0 && DateTimeOffset.TryParse(newest, out var at)
            && DateTimeOffset.UtcNow - at < TimeSpan.FromHours(6);
        var lastAttemptText = db.Scalar<string>("SELECT value FROM settings WHERE key='market_snapshot_attempt_at'");
        var coolingDown = DateTimeOffset.TryParse(lastAttemptText, out var lastAttempt)
            && DateTimeOffset.UtcNow - lastAttempt < TimeSpan.FromMinutes(30);

        LastSnapshotWarning = pricedCount == 0 && rowCount > 0
            ? "Bulk price history is unavailable; Ducat metadata and named-item live orders remain available."
            : !fresh && coolingDown && rowCount > 0
                ? "Bulk price refresh is cooling down after a recent failure; using the last cached snapshot."
                : null;
        if (_snapshotByName is not null && !force && (fresh || coolingDown)) return _snapshotByName;

        if ((force || !fresh) && (force || !coolingDown))
        {
            db.Exec("INSERT INTO settings(key,value) VALUES('market_snapshot_attempt_at',@v) ON CONFLICT(key) DO UPDATE SET value=@v",
                ("@v", DateTimeOffset.UtcNow.ToString("O")));
            try
            {
                await RefreshSnapshotAsync(ct, force);
                LastSnapshotWarning = null;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LastSnapshotWarning = $"Legacy bulk pricing is unavailable; using cached prices and v2 item metadata. {ex.Message}";
            }
        }

        var rows = db.Query("SELECT item_id,item_name,url_name,ducats,wa_price,median,fetched_at FROM price_snapshot",
            r => new PriceSnapshot
            {
                ItemId = r.GetString(0),
                ItemName = r.GetString(1),
                UrlName = r.GetString(2),
                Ducats = r.GetInt32(3),
                WaPrice = r.GetDouble(4),
                Median = r.GetDouble(5),
                FetchedAt = DateTimeOffset.Parse(r.GetString(6)),
            });
        if (rows.Count == 0)
            throw new InvalidOperationException(LastSnapshotWarning is null
                ? "No warframe.market price snapshot is available."
                : $"No warframe.market price snapshot is available. {LastSnapshotWarning}");
        _snapshotByName = rows
            .GroupBy(x => x.ItemName.ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.First());
        return _snapshotByName;
    }

    private async Task RefreshSnapshotAsync(CancellationToken ct, bool bypassCache = false)
    {
        var needsMetadataSeed = db.Scalar<long>("SELECT COUNT(*) FROM price_snapshot") == 0;
        await EnsureItemCatalogAsync(force: needsMetadataSeed, ct: ct);
        var nameById = db.Query("SELECT id,item_name,url_name FROM market_items",
                r => (Id: r.GetString(0), Name: r.GetString(1), Url: r.GetString(2)))
            .ToDictionary(x => x.Id, x => x);

        using var doc = await GetJsonAsync("/v1/tools/ducats", ct, cacheTtl: bypassCache ? null : TimeSpan.FromMinutes(30));
        var payload = doc.RootElement.GetProperty("payload");
        // previous_day has the wider sample; fill gaps from previous_hour.
        var merged = new Dictionary<string, JsonElement>();
        foreach (var key in new[] { "previous_hour", "previous_day" })
            if (payload.TryGetProperty(key, out var arr))
                foreach (var el in arr.EnumerateArray())
                    merged[el.GetProperty("item").GetString()!] = el;

        if (merged.Count < 100)
            throw new InvalidDataException($"warframe.market returned only {merged.Count} ducat rows; keeping the current snapshot.");

        var now = DateTimeOffset.UtcNow.ToString("O");
        db.Bulk((c, tx) =>
        {
            using (var clear = c.CreateCommand())
            {
                clear.Transaction = tx;
                clear.CommandText = "DELETE FROM price_snapshot";
                clear.ExecuteNonQuery();
            }
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO price_snapshot(item_id,item_name,url_name,ducats,wa_price,median,fetched_at)
                VALUES(@i,@n,@u,@d,@w,@m,@f)
                ON CONFLICT(item_id) DO UPDATE SET ducats=@d, wa_price=@w, median=@m, fetched_at=@f
                """;
            var pi = cmd.Parameters.Add("@i", SqliteType.Text);
            var pn = cmd.Parameters.Add("@n", SqliteType.Text);
            var pu = cmd.Parameters.Add("@u", SqliteType.Text);
            var pd = cmd.Parameters.Add("@d", SqliteType.Integer);
            var pw = cmd.Parameters.Add("@w", SqliteType.Real);
            var pm = cmd.Parameters.Add("@m", SqliteType.Real);
            var pf = cmd.Parameters.Add("@f", SqliteType.Text);
            foreach (var (id, el) in merged)
            {
                if (!nameById.TryGetValue(id, out var meta)) continue;
                pi.Value = id;
                pn.Value = meta.Name;
                pu.Value = meta.Url;
                pd.Value = el.TryGetProperty("ducats", out var d) ? d.GetInt32() : 0;
                pw.Value = el.TryGetProperty("wa_price", out var w) && w.ValueKind == JsonValueKind.Number ? w.GetDouble() : 0;
                pm.Value = el.TryGetProperty("median", out var m) && m.ValueKind == JsonValueKind.Number ? m.GetDouble() : 0;
                pf.Value = now;
                cmd.ExecuteNonQuery();
            }
        });
        _snapshotByName = null; // invalidate in-memory index
    }

    /// <summary>Lookup by exact item name (case-insensitive). Snapshot must be loaded first.</summary>
    public PriceSnapshot? LookupPrice(string itemName) =>
        _snapshotByName is not null && _snapshotByName.TryGetValue(itemName.ToLowerInvariant(), out var s) ? s : null;

    // ---- per-item detail --------------------------------------------------

    public async Task<List<MarketOrder>> GetOrdersAsync(string urlName, CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync($"/v2/orders/item/{urlName}", ct, cacheTtl: TimeSpan.FromSeconds(75));
        var list = new List<MarketOrder>();
        foreach (var el in doc.RootElement.GetProperty("data").EnumerateArray())
        {
            if (el.TryGetProperty("visible", out var vis) && !vis.GetBoolean()) continue;
            if (!el.TryGetProperty("user", out var user)) continue;
            list.Add(new MarketOrder
            {
                OrderType = el.GetProperty("type").GetString() ?? "",
                Platinum = (int)el.GetProperty("platinum").GetDouble(),
                Quantity = el.TryGetProperty("quantity", out var q) ? q.GetInt32() : 1,
                UserName = user.TryGetProperty("ingameName", out var un) ? un.GetString() ?? "" : "",
                UserStatus = user.TryGetProperty("status", out var st) ? st.GetString() ?? "" : "",
                ModRank = el.TryGetProperty("rank", out var mr) ? mr.GetInt32() : null,
            });
        }

        var itemName = db.Scalar<string>("SELECT item_name FROM market_items WHERE url_name=@u", ("@u", urlName)) ?? urlName;
        var active = list.Where(o => o.UserStatus is "ingame" or "online").ToList();
        var sells = active.Where(o => o.OrderType == "sell").ToList();
        var buys = active.Where(o => o.OrderType == "buy").ToList();
        var lowestSell = sells.Count > 0 ? sells.Min(o => o.Platinum) : 0;
        var ingameSells = sells.Where(o => o.UserStatus == "ingame").ToList();
        var lowestIngame = ingameSells.Count > 0 ? ingameSells.Min(o => o.Platinum) : 0;
        var highestBuy = buys.Count > 0 ? buys.Max(o => o.Platinum) : 0;
        db.Exec("""
            INSERT INTO market_live_metrics(url_name,item_name,lowest_sell,lowest_ingame_sell,highest_buy,sell_count,buy_count,fetched_at)
            VALUES(@u,@n,@s,@i,@b,@sc,@bc,@f)
            ON CONFLICT(url_name) DO UPDATE SET item_name=@n,lowest_sell=@s,lowest_ingame_sell=@i,
                highest_buy=@b,sell_count=@sc,buy_count=@bc,fetched_at=@f
            """, ("@u", urlName), ("@n", itemName), ("@s", lowestSell), ("@i", lowestIngame),
            ("@b", highestBuy), ("@sc", sells.Count), ("@bc", buys.Count),
            ("@f", DateTimeOffset.UtcNow.ToString("O")));
        return list;
    }

    public MarketLiveMetric? GetLiveMetric(string urlName) => db.Query("""
        SELECT url_name,item_name,lowest_sell,lowest_ingame_sell,highest_buy,sell_count,buy_count,fetched_at
        FROM market_live_metrics WHERE url_name=@u LIMIT 1
        """, r => new MarketLiveMetric
        {
            UrlName = r.GetString(0), ItemName = r.GetString(1), LowestSell = r.GetDouble(2),
            LowestIngameSell = r.GetDouble(3), HighestBuy = r.GetDouble(4), SellCount = r.GetInt32(5),
            BuyCount = r.GetInt32(6), FetchedAt = DateTimeOffset.Parse(r.GetString(7)),
        }, ("@u", urlName)).FirstOrDefault();

    public async Task<List<PricePoint>> GetStatisticsAsync(string urlName, CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync($"/v1/items/{urlName}/statistics", ct, cacheTtl: TimeSpan.FromHours(3));
        var list = new List<PricePoint>();
        var closed = doc.RootElement.GetProperty("payload").GetProperty("statistics_closed");
        if (closed.TryGetProperty("90days", out var days))
            foreach (var el in days.EnumerateArray())
                list.Add(new PricePoint
                {
                    Date = DateTimeOffset.Parse(el.GetProperty("datetime").GetString()!),
                    Median = el.GetProperty("median").GetDouble(),
                    AvgPrice = el.GetProperty("avg_price").GetDouble(),
                    Volume = el.GetProperty("volume").GetInt32(),
                });
        return list;
    }

    public async Task<ItemDetails?> GetItemDetailsAsync(string urlName, CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync($"/v2/item/{urlName}", ct, cacheTtl: TimeSpan.FromHours(24));
        var el = doc.RootElement.GetProperty("data");
        var en = el.TryGetProperty("i18n", out var i18n) && i18n.TryGetProperty("en", out var e) ? e : default;
        bool? vaulted = el.TryGetProperty("vaulted", out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : el.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array
                ? tags.EnumerateArray().Any(t => t.GetString() == "vaulted") ? true : null
                : null;
        return new ItemDetails
        {
            UrlName = urlName,
            ItemName = en.ValueKind == JsonValueKind.Object && en.TryGetProperty("name", out var nm) ? nm.GetString() ?? urlName : urlName,
            Ducats = el.TryGetProperty("ducats", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetInt32() : null,
            Vaulted = vaulted,
            MasteryLevel = el.TryGetProperty("reqMasteryRank", out var ml) && ml.ValueKind == JsonValueKind.Number ? ml.GetInt32() : null,
            TradingTax = el.TryGetProperty("tradingTax", out var tt) && tt.ValueKind == JsonValueKind.Number ? tt.GetInt32() : null,
            WikiLink = en.ValueKind == JsonValueKind.Object && en.TryGetProperty("wikiLink", out var wl) ? wl.GetString() : null,
            MaxRank = el.TryGetProperty("maxRank", out var mr) && mr.ValueKind == JsonValueKind.Number ? mr.GetInt32()
                : el.TryGetProperty("mod_max_rank", out var legacyMr) && legacyMr.ValueKind == JsonValueKind.Number ? legacyMr.GetInt32() : null,
        };
    }

    // ---- rivens -------------------------------------------------------------

    public async Task<List<(string UrlName, string ItemName)>> GetRivenWeaponsAsync(CancellationToken ct = default)
    {
        if (_rivenWeapons is not null) return _rivenWeapons;
        using var doc = await GetJsonAsync("/v2/riven/weapons", ct, cacheTtl: TimeSpan.FromHours(24));
        _rivenWeapons = doc.RootElement.GetProperty("data").EnumerateArray()
            .Select(el => (
                el.TryGetProperty("slug", out var slug) ? slug.GetString() ?? "" : "",
                el.TryGetProperty("i18n", out var i18n) && i18n.TryGetProperty("en", out var en) &&
                en.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : ""))
            .Where(x => x.Item1.Length > 0 && x.Item2.Length > 0)
            .OrderBy(x => x.Item2)
            .ToList();
        return _rivenWeapons;
    }

    public async Task<List<RivenAuctionInfo>> SearchRivenAuctionsAsync(string weaponUrlName, CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync(
            $"/v1/auctions/search?type=riven&weapon_url_name={weaponUrlName}&buyout_policy=direct&sort_by=price_asc", ct, cacheTtl: TimeSpan.FromSeconds(90));
        var list = new List<RivenAuctionInfo>();
        foreach (var el in doc.RootElement.GetProperty("payload").GetProperty("auctions").EnumerateArray())
        {
            var item = el.GetProperty("item");
            var info = new RivenAuctionInfo
            {
                WeaponUrlName = weaponUrlName,
                Name = item.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "",
                BuyoutPrice = el.TryGetProperty("buyout_price", out var bp) && bp.ValueKind == JsonValueKind.Number ? bp.GetInt32() : null,
                StartingPrice = el.TryGetProperty("starting_price", out var sp) && sp.ValueKind == JsonValueKind.Number ? sp.GetInt32() : 0,
                TopBid = el.TryGetProperty("top_bid", out var tb) && tb.ValueKind == JsonValueKind.Number ? tb.GetInt32() : null,
                ModRank = item.TryGetProperty("mod_rank", out var mr) ? mr.GetInt32() : 0,
                Rerolls = item.TryGetProperty("re_rolls", out var rr) ? rr.GetInt32() : 0,
                MasteryLevel = item.TryGetProperty("mastery_level", out var ml) ? ml.GetInt32() : 0,
                Polarity = item.TryGetProperty("polarity", out var pol) ? pol.GetString() ?? "" : "",
                OwnerStatus = el.TryGetProperty("owner", out var ow) && ow.TryGetProperty("status", out var st) ? st.GetString() ?? "" : "",
            };
            if (item.TryGetProperty("attributes", out var attrs) && attrs.ValueKind == JsonValueKind.Array)
                foreach (var a in attrs.EnumerateArray())
                    info.Attributes.Add(new RivenAttrInfo
                    {
                        UrlName = a.GetProperty("url_name").GetString() ?? "",
                        Value = a.GetProperty("value").GetDouble(),
                        Positive = a.TryGetProperty("positive", out var p) && p.GetBoolean(),
                    });
            list.Add(info);
        }
        return list;
    }

    // ---- authenticated account (profile slug + access token) ----------------

    /// <summary>Validate an access token and return the signed-in identity (also used to auto-fill the canonical profile slug).</summary>
    public async Task<MarketMe> GetMeAsync(string jwt, CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync("/v2/me", ct, jwt);
        var d = doc.RootElement.GetProperty("data");
        // v2 nests the account under "user" on some deployments; tolerate both shapes.
        var u = d.TryGetProperty("user", out var user) ? user : d;
        var me = new MarketMe
        {
            IngameName = u.TryGetProperty("ingameName", out var n) ? n.GetString() ?? "" : "",
            Slug = u.TryGetProperty("slug", out var s) ? s.GetString() ?? "" : "",
            Reputation = u.TryGetProperty("reputation", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetInt32() : 0,
            Platform = u.TryGetProperty("platform", out var p) ? p.GetString() : null,
            Locale = u.TryGetProperty("locale", out var l) ? l.GetString() : null,
            Role = u.TryGetProperty("role", out var ro) ? ro.GetString() : null,
            Platinum = u.TryGetProperty("platinum", out var pl) && pl.ValueKind == JsonValueKind.Number ? pl.GetInt32() : null,
        };
        Me = me;
        return me;
    }

    /// <summary>
    /// The user's own listings via the public /v2/orders/user/{slug} endpoint (no auth needed).
    /// Item names come from the local catalog; a reference market price is joined where available.
    /// </summary>
    public async Task<List<UserMarketOrder>> GetUserOrdersAsync(string slug, CancellationToken ct = default)
    {
        await EnsureItemCatalogAsync(ct: ct);
        var nameById = db.Query("SELECT id,item_name,url_name FROM market_items",
                r => (Id: r.GetString(0), Name: r.GetString(1), Url: r.GetString(2)))
            .ToDictionary(x => x.Id, x => (x.Name, x.Url));
        var priceById = db.Query("SELECT item_id,wa_price FROM price_snapshot",
                r => (Id: r.GetString(0), Price: r.GetDouble(1)))
            .ToDictionary(x => x.Id, x => x.Price);

        using var doc = await GetJsonAsync($"/v2/orders/user/{slug.Trim().ToLowerInvariant()}", ct, cacheTtl: TimeSpan.FromSeconds(90));
        var list = new List<UserMarketOrder>();
        foreach (var el in doc.RootElement.GetProperty("data").EnumerateArray())
        {
            var itemId = el.TryGetProperty("itemId", out var iid) ? iid.GetString() ?? "" : "";
            var (name, url) = nameById.TryGetValue(itemId, out var meta) ? meta : (itemId, "");
            list.Add(new UserMarketOrder
            {
                OrderType = el.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "",
                ItemName = name,
                UrlName = url,
                Platinum = el.TryGetProperty("platinum", out var p) ? (int)p.GetDouble() : 0,
                Quantity = el.TryGetProperty("quantity", out var q) ? q.GetInt32() : 1,
                Visible = !el.TryGetProperty("visible", out var v) || v.GetBoolean(),
                Rank = el.TryGetProperty("rank", out var rk) && rk.ValueKind == JsonValueKind.Number ? rk.GetInt32() : null,
                MarketLow = priceById.TryGetValue(itemId, out var price) ? price : 0,
            });
        }
        MyOrders = list
            .OrderBy(o => o.OrderType)
            .ThenByDescending(o => o.Platinum)
            .ToList();
        AccountSyncedAt = DateTimeOffset.UtcNow;
        return MyOrders;
    }

    // ---- platinum ledger (trading analytics) --------------------------------

    public void AddTrade(TradeEntry t) =>
        db.Exec("INSERT INTO trades(date,item_name,quantity,platinum,is_sale,notes) VALUES(@d,@i,@q,@p,@s,@n)",
            ("@d", t.Date.ToString("O")), ("@i", t.ItemName), ("@q", t.Quantity),
            ("@p", t.Platinum), ("@s", t.IsSale ? 1 : 0), ("@n", t.Notes));

    public void DeleteTrade(long id) => db.Exec("DELETE FROM trades WHERE id=@i", ("@i", id));

    public List<TradeEntry> GetTrades(int limit = 500) =>
        db.Query("SELECT id,date,item_name,quantity,platinum,is_sale,notes FROM trades ORDER BY date DESC LIMIT @l",
            r => new TradeEntry
            {
                Id = r.GetInt64(0),
                Date = DateTimeOffset.Parse(r.GetString(1)),
                ItemName = r.GetString(2),
                Quantity = r.GetInt32(3),
                Platinum = r.GetInt32(4),
                IsSale = r.GetInt32(5) == 1,
                Notes = r.IsDBNull(6) ? null : r.GetString(6),
            }, ("@l", limit));

    public (int TotalIn, int TotalOut, int Net, int Count) GetLedgerStats()
    {
        var totalIn = (int)db.Scalar<long>("SELECT COALESCE(SUM(platinum),0) FROM trades WHERE is_sale=1");
        var totalOut = (int)db.Scalar<long>("SELECT COALESCE(SUM(platinum),0) FROM trades WHERE is_sale=0");
        var count = (int)db.Scalar<long>("SELECT COUNT(*) FROM trades");
        return (totalIn, totalOut, totalIn - totalOut, count);
    }
}
