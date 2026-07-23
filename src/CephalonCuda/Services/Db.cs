using System.IO;
using Microsoft.Data.Sqlite;

namespace CephalonCuda.Services;

/// <summary>
/// Durable local store for live manifests, market caches, player data, community imports and
/// the compact search index injected into the AI advisor. Schema upgrades are additive so an
/// existing cuda.db is migrated in place without erasing player data.
/// </summary>
public sealed class Db
{
    public const int SchemaVersion = 8;

    public static string AppDataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CephalonCuda");

    private readonly string _connString;
    public bool FtsAvailable { get; private set; }

    public Db()
    {
        Directory.CreateDirectory(AppDataDir);
        _connString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(AppDataDir, "cuda.db"),
            Cache = SqliteCacheMode.Shared,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        }.ToString();
        Init();
    }

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_connString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        cmd.ExecuteNonQuery();
        return c;
    }

    private void Init()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA temp_store=MEMORY;

            CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY, value TEXT);

            CREATE TABLE IF NOT EXISTS market_items(
                id TEXT PRIMARY KEY, item_name TEXT NOT NULL, url_name TEXT NOT NULL, thumb TEXT);
            CREATE INDEX IF NOT EXISTS ix_market_items_name ON market_items(item_name COLLATE NOCASE);
            CREATE UNIQUE INDEX IF NOT EXISTS ux_market_items_url ON market_items(url_name);

            CREATE TABLE IF NOT EXISTS price_snapshot(
                item_id TEXT PRIMARY KEY, item_name TEXT NOT NULL, url_name TEXT NOT NULL,
                ducats INTEGER NOT NULL DEFAULT 0, wa_price REAL NOT NULL DEFAULT 0,
                median REAL NOT NULL DEFAULT 0, fetched_at TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_price_snapshot_name ON price_snapshot(item_name COLLATE NOCASE);
            CREATE INDEX IF NOT EXISTS ix_price_snapshot_fetched ON price_snapshot(fetched_at);

            CREATE TABLE IF NOT EXISTS api_cache(
                cache_key TEXT PRIMARY KEY, payload TEXT NOT NULL, fetched_at TEXT NOT NULL,
                expires_at TEXT NOT NULL, etag TEXT, last_modified TEXT);
            CREATE INDEX IF NOT EXISTS ix_api_cache_expiry ON api_cache(expires_at);

            CREATE TABLE IF NOT EXISTS market_live_metrics(
                url_name TEXT PRIMARY KEY, item_name TEXT NOT NULL,
                lowest_sell REAL NOT NULL DEFAULT 0, lowest_ingame_sell REAL NOT NULL DEFAULT 0,
                highest_buy REAL NOT NULL DEFAULT 0, sell_count INTEGER NOT NULL DEFAULT 0,
                buy_count INTEGER NOT NULL DEFAULT 0, fetched_at TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_market_metrics_name ON market_live_metrics(item_name COLLATE NOCASE);

            CREATE TABLE IF NOT EXISTS game_items(
                unique_name TEXT PRIMARY KEY, name TEXT NOT NULL, category TEXT,
                item_type TEXT, product_category TEXT, description TEXT,
                mastery_req INTEGER NOT NULL DEFAULT 0, tradable INTEGER NOT NULL DEFAULT 0,
                vaulted INTEGER, wiki_url TEXT, tags TEXT, summary TEXT NOT NULL,
                raw_json TEXT NOT NULL, fetched_at TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_game_items_name ON game_items(name COLLATE NOCASE);
            CREATE INDEX IF NOT EXISTS ix_game_items_category ON game_items(category, item_type);
            CREATE INDEX IF NOT EXISTS ix_game_items_tradable ON game_items(tradable, vaulted);

            CREATE TABLE IF NOT EXISTS official_drops(
                id TEXT PRIMARY KEY, item_name TEXT NOT NULL, source_type TEXT NOT NULL,
                location TEXT, rotation TEXT, chance REAL, rarity TEXT, detail TEXT,
                fetched_at TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_official_drops_item ON official_drops(item_name COLLATE NOCASE);
            CREATE INDEX IF NOT EXISTS ix_official_drops_source ON official_drops(source_type, location);

            CREATE TABLE IF NOT EXISTS player_sessions(
                id INTEGER PRIMARY KEY AUTOINCREMENT, started_at TEXT NOT NULL, ended_at TEXT,
                player_name TEXT, first_node TEXT, last_node TEXT, mission_count INTEGER NOT NULL DEFAULT 0,
                source TEXT NOT NULL DEFAULT 'EE.log');
            CREATE INDEX IF NOT EXISTS ix_player_sessions_started ON player_sessions(started_at DESC);

            CREATE TABLE IF NOT EXISTS community_tiers(
                id TEXT PRIMARY KEY, source_name TEXT NOT NULL, category TEXT NOT NULL,
                item_name TEXT NOT NULL, tier TEXT NOT NULL, rank_value INTEGER,
                votes INTEGER, score REAL, patch TEXT, notes TEXT, source_url TEXT,
                source_updated_at TEXT, imported_at TEXT NOT NULL, raw_json TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_community_tiers_item ON community_tiers(item_name COLLATE NOCASE);
            CREATE INDEX IF NOT EXISTS ix_community_tiers_source ON community_tiers(source_name, category);

            CREATE TABLE IF NOT EXISTS community_builds(
                id TEXT PRIMARY KEY, source_name TEXT NOT NULL, item_name TEXT NOT NULL,
                build_name TEXT NOT NULL, author TEXT, score REAL, votes INTEGER,
                forma INTEGER, patch TEXT, tags TEXT, mods TEXT, guide TEXT,
                source_url TEXT, source_updated_at TEXT, imported_at TEXT NOT NULL,
                raw_json TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_community_builds_item ON community_builds(item_name COLLATE NOCASE);
            CREATE INDEX IF NOT EXISTS ix_community_builds_source ON community_builds(source_name);
            CREATE INDEX IF NOT EXISTS ix_community_builds_score ON community_builds(score DESC, votes DESC);

            CREATE TABLE IF NOT EXISTS source_state(
                source_key TEXT PRIMARY KEY, display_name TEXT NOT NULL, status TEXT NOT NULL,
                item_count INTEGER NOT NULL DEFAULT 0, last_success_at TEXT,
                last_attempt_at TEXT, next_refresh_at TEXT, error TEXT, source_url TEXT,
                etag TEXT, last_modified TEXT);

            CREATE TABLE IF NOT EXISTS roadmap_tasks(
                id TEXT PRIMARY KEY, phase TEXT NOT NULL, category TEXT NOT NULL,
                title TEXT NOT NULL, description TEXT NOT NULL, why_it_matters TEXT NOT NULL,
                action_hint TEXT, source_url TEXT, sort_order INTEGER NOT NULL,
                minimum_mr INTEGER NOT NULL DEFAULT 0, optional INTEGER NOT NULL DEFAULT 0);
            CREATE INDEX IF NOT EXISTS ix_roadmap_tasks_order ON roadmap_tasks(sort_order);

            CREATE TABLE IF NOT EXISTS roadmap_progress(
                task_id TEXT PRIMARY KEY REFERENCES roadmap_tasks(id) ON DELETE CASCADE,
                completed INTEGER NOT NULL DEFAULT 0, skipped INTEGER NOT NULL DEFAULT 0,
                pinned INTEGER NOT NULL DEFAULT 0, completed_at TEXT, notes TEXT);

            CREATE TABLE IF NOT EXISTS player_goals(
                id INTEGER PRIMARY KEY AUTOINCREMENT, title TEXT NOT NULL, target_name TEXT,
                category TEXT NOT NULL DEFAULT 'General', priority INTEGER NOT NULL DEFAULT 2,
                status TEXT NOT NULL DEFAULT 'active', notes TEXT, created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_player_goals_status ON player_goals(status, priority, updated_at DESC);

            CREATE TABLE IF NOT EXISTS shop_rules(
                id TEXT PRIMARY KEY, keywords TEXT NOT NULL, verdict TEXT NOT NULL, severity INTEGER NOT NULL,
                title TEXT NOT NULL, reason TEXT NOT NULL, better_option TEXT NOT NULL, source_url TEXT);

            CREATE TABLE IF NOT EXISTS redeem_codes(
                code TEXT PRIMARY KEY, title TEXT, reward TEXT, source_url TEXT,
                expires_at TEXT, status TEXT NOT NULL DEFAULT 'unknown', claimed INTEGER NOT NULL DEFAULT 0,
                added_at TEXT NOT NULL, verified_at TEXT, notes TEXT);
            CREATE INDEX IF NOT EXISTS ix_redeem_codes_state ON redeem_codes(claimed, status, expires_at);

            CREATE TABLE IF NOT EXISTS inventory(
                id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, quantity INTEGER NOT NULL DEFAULT 1,
                notes TEXT, updated_at TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_inventory_name ON inventory(name COLLATE NOCASE);

            CREATE TABLE IF NOT EXISTS foundry_jobs(
                id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, ends_at TEXT NOT NULL,
                duration_minutes INTEGER NOT NULL, claimed INTEGER NOT NULL DEFAULT 0);
            CREATE INDEX IF NOT EXISTS ix_foundry_active ON foundry_jobs(claimed, ends_at);

            CREATE TABLE IF NOT EXISTS mastery_items(
                name TEXT PRIMARY KEY, kind TEXT NOT NULL, type TEXT,
                mastery_req INTEGER NOT NULL DEFAULT 0, mastered INTEGER NOT NULL DEFAULT 0);
            CREATE INDEX IF NOT EXISTS ix_mastery_kind ON mastery_items(kind, mastered);

            CREATE TABLE IF NOT EXISTS trades(
                id INTEGER PRIMARY KEY AUTOINCREMENT, date TEXT NOT NULL, item_name TEXT NOT NULL,
                quantity INTEGER NOT NULL DEFAULT 1, platinum INTEGER NOT NULL,
                is_sale INTEGER NOT NULL DEFAULT 1, notes TEXT);
            CREATE INDEX IF NOT EXISTS ix_trades_date ON trades(date DESC);
            CREATE INDEX IF NOT EXISTS ix_trades_item ON trades(item_name COLLATE NOCASE);

            CREATE TABLE IF NOT EXISTS chat_messages(
                id INTEGER PRIMARY KEY AUTOINCREMENT, role TEXT NOT NULL, content TEXT NOT NULL,
                created_at TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_chat_messages_created ON chat_messages(created_at DESC);

            CREATE TABLE IF NOT EXISTS ai_cache(
                hash TEXT PRIMARY KEY, response TEXT NOT NULL, created_at TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_ai_cache_created ON ai_cache(created_at);

            CREATE TABLE IF NOT EXISTS log_events(
                id INTEGER PRIMARY KEY AUTOINCREMENT, time TEXT NOT NULL, kind TEXT NOT NULL, detail TEXT);
            CREATE INDEX IF NOT EXISTS ix_log_events_time ON log_events(time DESC);

            CREATE TABLE IF NOT EXISTS ai_knowledge_fallback(
                source TEXT NOT NULL, external_id TEXT NOT NULL, name TEXT NOT NULL,
                category TEXT, body TEXT NOT NULL, tags TEXT,
                PRIMARY KEY(source, external_id));
            CREATE INDEX IF NOT EXISTS ix_ai_knowledge_name ON ai_knowledge_fallback(name COLLATE NOCASE);

            PRAGMA user_version=8;
            """;
        cmd.ExecuteNonQuery();
        EnsureColumn(c, "source_state", "etag", "TEXT");
        EnsureColumn(c, "source_state", "last_modified", "TEXT");

        // Microsoft.Data.Sqlite normally bundles FTS5. Keep a transparent fallback for unusual
        // system-SQLite deployments rather than making startup fail.
        try
        {
            cmd.CommandText = """
                CREATE VIRTUAL TABLE IF NOT EXISTS ai_knowledge_fts USING fts5(
                    source UNINDEXED,
                    external_id UNINDEXED,
                    name,
                    category,
                    body,
                    tags,
                    tokenize='unicode61 remove_diacritics 2'
                );
                """;
            cmd.ExecuteNonQuery();
            FtsAvailable = true;
        }
        catch (SqliteException)
        {
            FtsAvailable = false;
        }
    }

    private static void EnsureColumn(SqliteConnection c, string table, string column, string declaration)
    {
        using var check = c.CreateCommand();
        check.CommandText = $"PRAGMA table_info({table})";
        using var reader = check.ExecuteReader();
        while (reader.Read())
            if (reader.GetString(1).Equals(column, StringComparison.OrdinalIgnoreCase))
                return;
        reader.Close();
        using var alter = c.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {declaration}";
        alter.ExecuteNonQuery();
    }

    // ---- tiny ADO helpers -------------------------------------------------

    public int Exec(string sql, params (string Name, object? Value)[] args)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd.ExecuteNonQuery();
    }

    public T? Scalar<T>(string sql, params (string Name, object? Value)[] args)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        var result = cmd.ExecuteScalar();
        if (result is null || result is DBNull) return default;
        var targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        return (T)Convert.ChangeType(result, targetType);
    }

    public List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string Name, object? Value)[] args)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        using var reader = cmd.ExecuteReader();
        var list = new List<T>();
        while (reader.Read()) list.Add(map(reader));
        return list;
    }

    public void Bulk(Action<SqliteConnection, SqliteTransaction> work)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        work(c, tx);
        tx.Commit();
    }

    public void Checkpoint()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            DELETE FROM api_cache WHERE expires_at < @apiCutoff;
            DELETE FROM ai_cache WHERE created_at < @aiCutoff;
            DELETE FROM market_live_metrics WHERE fetched_at < @marketCutoff;
            DELETE FROM log_events WHERE time < @logCutoff;
            DELETE FROM player_sessions WHERE started_at < @sessionCutoff;
            PRAGMA wal_checkpoint(PASSIVE);
            PRAGMA optimize;
            """;
        var now = DateTimeOffset.UtcNow;
        cmd.Parameters.AddWithValue("@apiCutoff", now.AddDays(-7).ToString("O"));
        cmd.Parameters.AddWithValue("@aiCutoff", now.AddDays(-30).ToString("O"));
        cmd.Parameters.AddWithValue("@marketCutoff", now.AddDays(-14).ToString("O"));
        cmd.Parameters.AddWithValue("@logCutoff", now.AddDays(-90).ToString("O"));
        cmd.Parameters.AddWithValue("@sessionCutoff", now.AddYears(-2).ToString("O"));
        cmd.ExecuteNonQuery();
    }
}
