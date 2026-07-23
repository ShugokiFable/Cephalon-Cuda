using CephalonCuda.Models;

namespace CephalonCuda.Services;

public sealed class FoundryService(Db db)
{
    /// <summary>Common Warframe build durations, minutes.</summary>
    public static readonly (string Label, int Minutes)[] Presets =
    [
        ("Warframe (72h)", 4320),
        ("Warframe part (12h)", 720),
        ("Weapon (24h)", 1440),
        ("Weapon part (12h)", 720),
        ("Forma (23h)", 1380),
        ("Orokin Catalyst (24h)", 1440),
        ("Orokin Reactor (24h)", 1440),
        ("Exilus Adapter (24h)", 1440),
        ("Necramech (72h)", 4320),
        ("K-Drive (24h)", 1440),
        ("Custom (1h)", 60),
    ];

    public List<FoundryJob> GetAll(bool includeClaimed = false) => db.Query(
        includeClaimed
            ? "SELECT id,name,ends_at,duration_minutes,claimed FROM foundry_jobs ORDER BY ends_at"
            : "SELECT id,name,ends_at,duration_minutes,claimed FROM foundry_jobs WHERE claimed=0 ORDER BY ends_at",
        r => new FoundryJob
        {
            Id = r.GetInt64(0),
            Name = r.GetString(1),
            EndsAt = DateTimeOffset.Parse(r.GetString(2)),
            DurationMinutes = r.GetInt32(3),
            Claimed = r.GetInt32(4) == 1,
        });

    public void Add(string name, int minutes, DateTimeOffset? startedAt = null)
    {
        var ends = (startedAt ?? DateTimeOffset.Now).AddMinutes(minutes);
        db.Exec("INSERT INTO foundry_jobs(name,ends_at,duration_minutes) VALUES(@n,@e,@d)",
            ("@n", name.Trim()), ("@e", ends.ToString("O")), ("@d", minutes));
    }

    public void Claim(long id) => db.Exec("UPDATE foundry_jobs SET claimed=1 WHERE id=@i", ("@i", id));
    public void Delete(long id) => db.Exec("DELETE FROM foundry_jobs WHERE id=@i", ("@i", id));
}
