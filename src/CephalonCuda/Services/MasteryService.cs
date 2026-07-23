using System.Net.Http;
using System.Text.Json;
using CephalonCuda.Models;
using Microsoft.Data.Sqlite;

namespace CephalonCuda.Services;

/// <summary>
/// Mastery checklist backed by api.warframestat.us item exports (warframes + weapons).
/// MR shown here is an estimate from tracked equipment only — star chart, intrinsics and
/// junctions also grant mastery, so the user can override their true MR in Settings.
/// </summary>
public sealed class MasteryService(HttpClient http, Db db)
{
    public async Task<int> SyncCatalogAsync(CancellationToken ct = default)
    {
        int n = 0;
        n += await SyncEndpointAsync("https://api.warframestat.us/warframes/?language=en", "Warframe", ct);
        n += await SyncEndpointAsync("https://api.warframestat.us/weapons/?language=en", "Weapon", ct);
        return n;
    }

    private async Task<int> SyncEndpointAsync(string url, string kind, CancellationToken ct)
    {
        var json = await http.GetStringAsync(url, ct);
        using var doc = JsonDocument.Parse(json);
        int n = 0;
        db.Bulk((c, tx) =>
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO mastery_items(name,kind,type,mastery_req) VALUES(@n,@k,@t,@m)
                ON CONFLICT(name) DO UPDATE SET kind=@k, type=@t, mastery_req=@m
                """;
            var pn = cmd.Parameters.Add("@n", SqliteType.Text);
            var pk = cmd.Parameters.Add("@k", SqliteType.Text);
            var pt = cmd.Parameters.Add("@t", SqliteType.Text);
            var pm = cmd.Parameters.Add("@m", SqliteType.Integer);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var name = el.TryGetProperty("name", out var nm) ? nm.GetString() : null;
                if (string.IsNullOrWhiteSpace(name)) continue;
                pn.Value = name;
                pk.Value = kind;
                pt.Value = el.TryGetProperty("type", out var ty) ? ty.GetString() as object ?? DBNull.Value : DBNull.Value;
                pm.Value = el.TryGetProperty("masteryReq", out var mr) && mr.ValueKind == JsonValueKind.Number ? mr.GetInt32() : 0;
                cmd.ExecuteNonQuery();
                n++;
            }
        });
        return n;
    }

    public List<MasteryItem> GetAll() => db.Query(
        "SELECT name,kind,type,mastery_req,mastered FROM mastery_items ORDER BY kind, name",
        r => new MasteryItem
        {
            Name = r.GetString(0),
            Kind = r.GetString(1),
            Type = r.IsDBNull(2) ? null : r.GetString(2),
            MasteryReq = r.GetInt32(3),
            Mastered = r.GetInt32(4) == 1,
        });

    public void SetMastered(string name, bool mastered) =>
        db.Exec("UPDATE mastery_items SET mastered=@m WHERE name=@n", ("@m", mastered ? 1 : 0), ("@n", name));

    public (long TrackedMastery, int EstimatedMr, int MasteredCount, int TotalCount) Stats()
    {
        var mastery = db.Scalar<long>(
            "SELECT COALESCE(SUM(CASE WHEN kind='Weapon' THEN 3000 ELSE 6000 END),0) FROM mastery_items WHERE mastered=1");
        var mastered = (int)db.Scalar<long>("SELECT COUNT(*) FROM mastery_items WHERE mastered=1");
        var total = (int)db.Scalar<long>("SELECT COUNT(*) FROM mastery_items");
        // Total mastery required to *reach* rank R is 2500 * R^2.
        var mr = (int)Math.Floor(Math.Sqrt(mastery / 2500.0));
        return (mastery, mr, mastered, total);
    }

    /// <summary>What to level next: unmastered items the player can already use at their MR.</summary>
    public List<MasteryItem> Suggestions(int currentMr, int limit = 30) => db.Query(
        """
        SELECT name,kind,type,mastery_req,mastered FROM mastery_items
        WHERE mastered=0 AND mastery_req <= @mr
        ORDER BY mastery_req DESC, kind DESC, name LIMIT @l
        """,
        r => new MasteryItem
        {
            Name = r.GetString(0),
            Kind = r.GetString(1),
            Type = r.IsDBNull(2) ? null : r.GetString(2),
            MasteryReq = r.GetInt32(3),
            Mastered = r.GetInt32(4) == 1,
        }, ("@mr", currentMr), ("@l", limit));
}
