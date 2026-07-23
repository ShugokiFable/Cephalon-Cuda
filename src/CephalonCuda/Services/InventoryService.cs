using CephalonCuda.Models;

namespace CephalonCuda.Services;

public sealed class InventoryService(Db db, MarketService market)
{
    public async Task<List<InventoryItem>> GetAllAsync(CancellationToken ct = default)
    {
        await market.GetPriceSnapshotAsync(ct: ct); // best effort; offline uses stale cache
        var rows = db.Query(
            "SELECT id,name,quantity,notes,updated_at FROM inventory ORDER BY name",
            r => new InventoryItem
            {
                Id = r.GetInt64(0),
                Name = r.GetString(1),
                Quantity = r.GetInt32(2),
                Notes = r.IsDBNull(3) ? null : r.GetString(3),
                UpdatedAt = DateTimeOffset.Parse(r.GetString(4)),
            });
        foreach (var item in rows)
        {
            var snap = market.LookupPrice(item.Name);
            item.UnitPlat = snap?.WaPrice ?? 0;
            item.UnitDucats = snap?.Ducats ?? 0;
        }
        return rows;
    }

    public void Upsert(string name, int quantity, string? notes = null)
    {
        var existing = db.Scalar<long?>("SELECT id FROM inventory WHERE lower(name)=lower(@n)", ("@n", name));
        if (existing is { } id)
            db.Exec("UPDATE inventory SET quantity=@q, notes=COALESCE(@o,notes), updated_at=@u WHERE id=@i",
                ("@q", quantity), ("@o", notes), ("@u", DateTimeOffset.UtcNow.ToString("O")), ("@i", id));
        else
            db.Exec("INSERT INTO inventory(name,quantity,notes,updated_at) VALUES(@n,@q,@o,@u)",
                ("@n", name.Trim()), ("@q", quantity), ("@o", notes), ("@u", DateTimeOffset.UtcNow.ToString("O")));
    }

    public void Delete(long id) => db.Exec("DELETE FROM inventory WHERE id=@i", ("@i", id));

    /// <summary>Delete by case-insensitive name match. Returns true if a row was removed.</summary>
    public bool DeleteByName(string name)
    {
        var id = db.Scalar<long?>("SELECT id FROM inventory WHERE lower(name)=lower(@n)", ("@n", name));
        if (id is { } i) { Delete(i); return true; }
        return false;
    }

    public (double TotalPlat, long TotalDucats, long ItemCount) Totals(List<InventoryItem> items) =>
        (items.Sum(i => i.TotalPlat), items.Sum(i => (long)i.TotalDucats), items.Sum(i => (long)i.Quantity));
}
