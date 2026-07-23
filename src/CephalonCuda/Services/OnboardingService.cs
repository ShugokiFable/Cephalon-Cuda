using System.IO;
using System.Diagnostics;
using System.Text.Json;
using CephalonCuda.Models;

namespace CephalonCuda.Services;

/// <summary>
/// Manual-first progression memory and purchase guardrails. Warframe exposes no supported public
/// account-progression API, so the app never fabricates completion state: the player checks tasks,
/// pins goals, or tells the advisor what changed. Curated rows are versioned and can be updated
/// additively without erasing personal progress.
/// </summary>
public sealed class OnboardingService
{
    public const string OfficialNewPlayerGuide = "https://www.warframe.com/en/news/new-player-guide";
    public const string OfficialQuestGuide = "https://www.warframe.com/en/guides/quests";
    public const string OfficialDrops = "https://warframe-web-assets.nyc3.cdn.digitaloceanspaces.com/uploads/cms/hnfvc0o3jnfvc873njb03enrf56.html";
    public const string OfficialRedeem = "https://www.warframe.com/promocode";

    private readonly Db _db;
    public OnboardingService(Db db)
    {
        _db = db;
        SeedRoadmap();
        SeedShopRules();
    }

    public List<RoadmapTask> GetRoadmap(bool includeFinished = true)
    {
        var where = includeFinished ? "" : "WHERE COALESCE(p.completed,0)=0 AND COALESCE(p.skipped,0)=0";
        return _db.Query($"""
            SELECT t.id,t.phase,t.category,t.title,t.description,t.why_it_matters,t.action_hint,t.source_url,
                   t.sort_order,t.minimum_mr,t.optional,COALESCE(p.completed,0),COALESCE(p.skipped,0),COALESCE(p.pinned,0)
            FROM roadmap_tasks t LEFT JOIN roadmap_progress p ON p.task_id=t.id
            {where}
            ORDER BY COALESCE(p.pinned,0) DESC, t.sort_order
            """, r => new RoadmapTask
        {
            Id = r.GetString(0), Phase = r.GetString(1), Category = r.GetString(2), Title = r.GetString(3),
            Description = r.GetString(4), WhyItMatters = r.GetString(5), ActionHint = r.IsDBNull(6) ? null : r.GetString(6),
            SourceUrl = r.IsDBNull(7) ? null : r.GetString(7), SortOrder = r.GetInt32(8), MinimumMr = r.GetInt32(9),
            Optional = r.GetInt32(10) != 0, Completed = r.GetInt32(11) != 0, Skipped = r.GetInt32(12) != 0,
            Pinned = r.GetInt32(13) != 0,
        });
    }

    public List<RoadmapTask> GetNextTasks(int limit = 6)
    {
        var mr = App.Services.Settings.MasteryRank;
        return GetRoadmap(false)
            .OrderByDescending(t => t.Pinned)
            .ThenBy(t => mr > 0 && t.MinimumMr > mr ? 1 : 0)
            .ThenBy(t => t.SortOrder)
            .Take(limit).ToList();
    }

    public void SetTaskState(string id, bool? completed = null, bool? skipped = null, bool? pinned = null)
    {
        _db.Exec("""
            INSERT INTO roadmap_progress(task_id,completed,skipped,pinned,completed_at)
            VALUES(@id,@done,@skip,@pin,@at)
            ON CONFLICT(task_id) DO UPDATE SET
              completed=CASE WHEN @setDone=1 THEN @done ELSE completed END,
              skipped=CASE WHEN @setSkip=1 THEN @skip ELSE skipped END,
              pinned=CASE WHEN @setPin=1 THEN @pin ELSE pinned END,
              completed_at=CASE WHEN @setDone=1 THEN @at ELSE completed_at END
            """,
            ("@id", id), ("@done", completed == true ? 1 : 0), ("@skip", skipped == true ? 1 : 0),
            ("@pin", pinned == true ? 1 : 0), ("@at", completed == true ? DateTimeOffset.UtcNow.ToString("O") : null),
            ("@setDone", completed.HasValue ? 1 : 0), ("@setSkip", skipped.HasValue ? 1 : 0),
            ("@setPin", pinned.HasValue ? 1 : 0));
    }

    public (int Done, int Total, int Percent) Progress()
    {
        var total = (int)_db.Scalar<long>("SELECT COUNT(*) FROM roadmap_tasks WHERE optional=0");
        var done = (int)_db.Scalar<long>("""
            SELECT COUNT(*) FROM roadmap_progress p JOIN roadmap_tasks t ON t.id=p.task_id
            WHERE p.completed=1 AND t.optional=0
            """);
        return (done, total, total == 0 ? 0 : (int)Math.Round(done * 100d / total));
    }

    public ShopVerdict EvaluatePurchase(string query)
    {
        query = (query ?? "").Trim();
        if (query.Length == 0) return new ShopVerdict();
        var normalized = query.ToLowerInvariant();
        var rows = _db.Query("SELECT id,keywords,verdict,severity,title,reason,better_option,source_url FROM shop_rules ORDER BY severity DESC",
            r => new
            {
                Id = r.GetString(0), Keywords = r.GetString(1), Verdict = r.GetString(2), Severity = r.GetInt32(3),
                Title = r.GetString(4), Reason = r.GetString(5), Better = r.GetString(6), Url = r.IsDBNull(7) ? null : r.GetString(7),
            });
        var hit = rows.FirstOrDefault(x => x.Keywords.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(k => normalized.Contains(k, StringComparison.OrdinalIgnoreCase)));

        // Match both a bare item name ("Rhino") and natural offer text ("Rhino for 375p").
        // Long names win when several records are contained in the same sentence.
        var item = _db.Query("""
            SELECT g.name,g.category,g.item_type,g.product_category,g.mastery_req,g.tradable,g.wiki_url,
                   COALESCE((SELECT p.wa_price FROM price_snapshot p WHERE lower(p.item_name)=lower(g.name) LIMIT 1),0)
            FROM game_items g
            WHERE lower(g.name)=lower(@q)
               OR lower(@q) LIKE '%'||lower(g.name)||'%'
               OR (length(@q) >= 4 AND lower(g.name) LIKE '%'||lower(@q)||'%')
            ORDER BY CASE WHEN lower(g.name)=lower(@q) THEN 0
                          WHEN lower(@q) LIKE '%'||lower(g.name)||'%' THEN 1 ELSE 2 END,
                     length(g.name) DESC
            LIMIT 1
            """, r => new
            {
                Name = r.GetString(0),
                Category = r.IsDBNull(1) ? "item" : r.GetString(1),
                ItemType = r.IsDBNull(2) ? "" : r.GetString(2),
                ProductCategory = r.IsDBNull(3) ? "" : r.GetString(3),
                Mr = r.GetInt32(4), Tradable = r.GetInt32(5) != 0,
                Wiki = r.IsDBNull(6) ? null : r.GetString(6), Price = r.GetDouble(7),
            }, ("@q", query)).FirstOrDefault();

        var intel = item is null ? null : $"Database match: {item.Name} · {item.Category} · MR {item.Mr}" +
            (item.Tradable ? " · tradable" : " · not player-tradable") + (item.Price > 0 ? $" · market snapshot ~{item.Price:0.#}p" : "");

        // Explicit wording rules take precedence, then the item database catches deceptively simple
        // offers such as entering only "Rhino" or "Boltor" into the checker.
        if (hit is null && item is not null)
        {
            var typeBlob = $"{item.Category} {item.ItemType} {item.ProductCategory}".ToLowerInvariant();
            var isPrime = item.Name.Contains(" Prime", StringComparison.OrdinalIgnoreCase);
            var isFrame = typeBlob.Contains("warframe") || typeBlob.Contains("frame");
            var isWeapon = typeBlob.Contains("weapon") || typeBlob.Contains("primary") || typeBlob.Contains("secondary") ||
                           typeBlob.Contains("melee") || typeBlob.Contains("archgun") || typeBlob.Contains("sentinel weapon");

            if (isFrame && !isPrime)
                return new ShopVerdict
                {
                    RuleId = "base-frame-database", Verdict = "AVOID", Severity = 90,
                    Title = $"Do not buy {item.Name} for Platinum by default",
                    Reason = "The item database identifies this as a regular Warframe. The Market price mainly skips its acquisition and build time, which is usually poor early-account value.",
                    BetterOption = "Ask for its exact farm route, check whether a Prime version is available from players, and reserve Platinum for slots unless the time saved is deliberately worth it to you.",
                    ItemIntel = intel,
                };
            if (isWeapon && !isPrime)
                return new ShopVerdict
                {
                    RuleId = "base-weapon-database", Verdict = "AVOID", Severity = 88,
                    Title = $"Craft {item.Name} instead of buying it by default",
                    Reason = "The database identifies this as ordinary equipment. Many such weapons come from Credit blueprints, clan research, quests, or deterministic drops; the bundled slot and Catalyst rarely justify the full markup for a new player.",
                    BetterOption = "Find the blueprint or source, craft it, and use Platinum only if the exact time saving beats the permanent value of slots.",
                    ItemIntel = intel,
                };
            if (isPrime && item.Tradable)
                return new ShopVerdict
                {
                    RuleId = "prime-market-compare", Verdict = "COMPARE", Severity = 45,
                    Title = $"Compare {item.Name} with live player-market orders",
                    Reason = "Prime equipment is commonly acquired through relics or player trading. An in-game Market bundle, Prime Access offer, and player-traded set are different products with different extras.",
                    BetterOption = "Check the live set/part price, owned relics, required Mastery Rank, and included cosmetics or Platinum before deciding.",
                    ItemIntel = intel,
                };
        }

        return hit is null
            ? new ShopVerdict { ItemIntel = intel }
            : new ShopVerdict
            {
                RuleId = hit.Id, Verdict = hit.Verdict, Severity = hit.Severity, Title = hit.Title,
                Reason = hit.Reason, BetterOption = hit.Better, ItemIntel = intel,
            };
    }

    public List<PlayerGoal> GetGoals(bool activeOnly = true) => _db.Query($"""
        SELECT id,title,COALESCE(target_name,''),category,priority,status,COALESCE(notes,'')
        FROM player_goals {(activeOnly ? "WHERE status='active'" : "")}
        ORDER BY CASE priority WHEN 1 THEN 0 WHEN 2 THEN 1 ELSE 2 END, updated_at DESC
        """, r => new PlayerGoal
    {
        Id = r.GetInt64(0), Title = r.GetString(1), TargetName = r.GetString(2), Category = r.GetString(3),
        Priority = r.GetInt32(4), Status = r.GetString(5), Notes = r.GetString(6),
    });

    public long AddGoal(string title, string? target, string category, int priority, string? notes)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        _db.Exec("""
            INSERT INTO player_goals(title,target_name,category,priority,status,notes,created_at,updated_at)
            VALUES(@t,@target,@c,@p,'active',@n,@now,@now)
            """, ("@t", title.Trim()), ("@target", target?.Trim()), ("@c", category),
            ("@p", Math.Clamp(priority, 1, 3)), ("@n", notes?.Trim()), ("@now", now));
        return _db.Scalar<long>("SELECT id FROM player_goals ORDER BY id DESC LIMIT 1");
    }

    public void CompleteGoal(long id) => _db.Exec("UPDATE player_goals SET status='done',updated_at=@u WHERE id=@id",
        ("@u", DateTimeOffset.UtcNow.ToString("O")), ("@id", id));
    public void DeleteGoal(long id) => _db.Exec("DELETE FROM player_goals WHERE id=@id", ("@id", id));

    public List<RedeemCodeEntry> GetCodes() => _db.Query("""
        SELECT code,COALESCE(title,''),COALESCE(reward,''),status,claimed,expires_at,source_url
        FROM redeem_codes ORDER BY claimed, CASE status WHEN 'active' THEN 0 WHEN 'unknown' THEN 1 ELSE 2 END, added_at DESC
        """, r => new RedeemCodeEntry
    {
        Code = r.GetString(0), Title = r.GetString(1), Reward = r.GetString(2), Status = r.GetString(3),
        Claimed = r.GetInt32(4) != 0, ExpiresAt = r.IsDBNull(5) ? null : r.GetString(5),
        SourceUrl = r.IsDBNull(6) ? null : r.GetString(6),
    });

    public void UpsertCode(string code, string? title, string? reward, string status = "unknown", string? expiresAt = null, string? sourceUrl = null)
    {
        code = code.Trim().ToUpperInvariant();
        if (code.Length is < 2 or > 100) throw new ArgumentException("Code must be between 2 and 100 characters.");
        _db.Exec("""
            INSERT INTO redeem_codes(code,title,reward,source_url,expires_at,status,claimed,added_at,verified_at)
            VALUES(@c,@t,@r,@u,@e,@s,0,@now,@now)
            ON CONFLICT(code) DO UPDATE SET title=@t,reward=@r,source_url=COALESCE(@u,source_url),
              expires_at=COALESCE(@e,expires_at),status=@s,verified_at=@now
            """, ("@c", code), ("@t", title), ("@r", reward), ("@u", SafeHttpUrl(sourceUrl)),
            ("@e", expiresAt), ("@s", status), ("@now", DateTimeOffset.UtcNow.ToString("O")));
    }

    public void SetCodeClaimed(string code, bool claimed) => _db.Exec(
        "UPDATE redeem_codes SET claimed=@v WHERE code=@c", ("@v", claimed ? 1 : 0), ("@c", code));

    public int ImportCodes(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("Code list was not found.", path);
        if (info.Length > 2_000_000) throw new InvalidDataException("Code list is larger than the 2 MB safety limit.");
        using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
            MaxDepth = 16,
        });
        var root = doc.RootElement;
        var array = root.ValueKind == JsonValueKind.Array ? root : root.TryGetProperty("codes", out var c) ? c : default;
        if (array.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Expected a JSON array or an object with a 'codes' array.");
        if (array.GetArrayLength() > 5_000) throw new InvalidDataException("Code list contains more than 5,000 entries.");
        var count = 0;
        foreach (var el in array.EnumerateArray())
        {
            var code = GetString(el, "code");
            if (string.IsNullOrWhiteSpace(code)) continue;
            var status = (GetString(el, "status") ?? "unknown").Trim().ToLowerInvariant();
            if (status is not ("active" or "unknown" or "expired")) status = "unknown";
            UpsertCode(code, Clip(GetString(el, "title"), 160), Clip(GetString(el, "reward"), 300),
                status, Clip(GetString(el, "expiresAt"), 80), GetString(el, "sourceUrl"));
            count++;
        }
        return count;
    }

    public object BuildAdvisorSummary()
    {
        var p = Progress();
        return new
        {
            roadmap = new { completed = p.Done, total = p.Total, percent = p.Percent, next = GetNextTasks(5).Select(t => $"{t.Title}: {t.ActionHint ?? t.Description}") },
            goals = GetGoals().Take(8).Select(g => new { g.Title, g.TargetName, g.Category, g.PriorityDisplay, g.Notes }),
            unclaimedCodes = GetCodes().Where(c => !c.Claimed && c.Status != "expired").Take(10).Select(c => new { c.Code, c.Title, c.Reward, c.Status, c.ExpiresAt }),
            shopGuard = "Treat Platinum as scarce. Check slots first; compare farmability and player-market price before progression purchases; do not rush by default.",
        };
    }

    public static void OpenUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    private void SeedRoadmap()
    {
        var tasks = new (string Id,string Phase,string Category,string Title,string Desc,string Why,string Hint,string? Url,int Order,int Mr,bool Optional)[]
        {
            ("foundation-vors-prize","1 · FOUNDATION","Quest","Finish Vor's Prize","Complete the tutorial quest and unlock the Orbiter's core systems.","The Arsenal, Mods, Foundry and Navigation are the spine of the entire game.","Follow the active quest marker and inspect every Orbiter console afterward.",OfficialNewPlayerGuide,10,0,false),
            ("foundation-mods","1 · FOUNDATION","Power","Learn mod capacity and upgrade a small core","Install damage, multishot, elemental and survivability mods; rank useful mods gradually instead of maxing everything blindly.","Most power comes from mods, not the weapon's unmodded stats.","Build one reliable loadout before spreading Endo and Credits across dozens of mods.",OfficialNewPlayerGuide,20,0,false),
            ("foundation-slots","1 · FOUNDATION","Platinum","Protect starter Platinum","Reserve early Platinum for Warframe and weapon slots. Do not buy Credits, resources, rush timers or ordinary base gear.","Slots permanently expand the account; most other early Market purchases replace a short farm with an expensive click.","Buy slots only when capacity blocks a crafted item you want to keep.",null,30,0,false),
            ("foundation-clan","1 · FOUNDATION","Social","Join an established clan","Join a clan with completed research and use the Dojo labs for blueprints.","A large catalog of weapons, frames and utilities is hidden behind clan research.","Ask Recruiting chat for a beginner-friendly clan with full research and no mandatory tax.",null,40,0,false),
            ("foundation-companion","1 · FOUNDATION","Quality of Life","Equip loot vacuum and radar","Use Vacuum on a Sentinel or Fetch on a beast; add loot/enemy radar when available.","Missing pickups and map information quietly wastes enormous amounts of time.","Check companion mods before every resource farm.",null,50,0,false),
            ("foundation-rhino","1 · FOUNDATION","Build Target","Craft an early durable Warframe","Farm an accessible early Warframe such as Rhino if your starter feels fragile.","A durable second frame makes Junctions and unfamiliar missions less punishing.","Farm the Jackal on Fossa, Venus for component blueprints; buy the main blueprint for Credits when available.",OfficialDrops,60,0,true),
            ("starchart-junctions","2 · STAR CHART","Progression","Push Junction requirements","Clear new nodes and complete Junction tasks instead of repeating the same farm indefinitely.","Junctions unlock planets, quests, systems and better resource access.","Pin the next Junction and work backward through only its unmet requirements.",OfficialQuestGuide,100,0,false),
            ("starchart-nightwave","2 · STAR CHART","Weekly","Unlock and use Nightwave","Complete easy acts and save Cred for scarce essentials such as Nitain Extract; evaluate other offerings by need.","Nightwave solves several early resource and upgrade bottlenecks.","Do passive acts first; never derail progression for a difficult act with poor time value.",null,110,0,false),
            ("starchart-relics","2 · STAR CHART","Economy","Learn relic refinement and reward selection","Run Void Fissures, understand intact versus refined relics, and choose rewards by ownership, rarity and trade value.","Prime parts become equipment, Ducats or Platinum; bad reward choices compound quickly.","Use the relic scanner or market lookup before selecting an unfamiliar reward.",null,120,2,false),
            ("starchart-syndicate","2 · STAR CHART","Reputation","Choose aligned Syndicates","Pledge to compatible main Syndicates and spend standing before hitting the daily cap.","Standing left capped is permanently lost time; augments and weapons have trade value.","Pick an allied pair first rather than trying to maintain all six.",null,130,3,false),
            ("starchart-master-fodder","2 · STAR CHART","Mastery","Cycle inexpensive blueprints for Mastery","Build Credit blueprints and clan research items, level each unique item once, then keep only what you value.","Mastery Rank unlocks weapons, capacity and daily limits.","Check the Mastery Helper before selling anything so you do not discard an unmastered or crafting-required item.",null,140,2,false),
            ("power-corrupted-mods","3 · POWER LAYER","Mods","Acquire corrupted mods","Open Orokin Vaults on Deimos with Dragon Keys and collect the major ability-stat tradeoff mods.","These mods enable the strength, duration, range and efficiency breakpoints used by most serious builds.","Run coordinated vault captures or exterminates; keep one copy of every unique mod.",OfficialDrops,200,5,false),
            ("power-potatoes-forma","3 · POWER LAYER","Upgrades","Use Catalysts, Reactors and Forma intentionally","Invest only in equipment you expect to keep or that unlocks meaningful progress.","These upgrades are account bottlenecks early on; random use creates regret.","Test the item at rank 30 and draft the final build before committing Forma.",null,210,4,false),
            ("power-quests","3 · POWER LAYER","Quest","Follow the main cinematic quest chain","Prioritize required quests shown by the official Quest Guide when progression stalls.","Major systems, locations and narrative content are quest-gated.","Use the app's quest link and track one main quest at a time.",OfficialQuestGuide,220,3,false),
            ("power-open-worlds","3 · POWER LAYER","Reputation","Treat open worlds as side tracks, not the whole game","Raise standing steadily but avoid camping one open world until burnout.","Open worlds contain useful systems, but overfarming them too early slows the Star Chart and quest unlocks.","Do a small daily standing block, then return to Junctions and quests.",null,230,3,false),
            ("power-necramech","3 · POWER LAYER","Prerequisite","Prepare New War requirements","Build the required vehicle and quest prerequisites through their intended progression path.","The New War and later systems are blocked until prerequisite gear is ready.","Ask the advisor for the exact current prerequisite list and the cheapest farm path for missing parts.",OfficialQuestGuide,240,5,false),
            ("late-arbitrations","4 · LATE GAME","Unlock","Unlock Arbitrations","Complete the required Star Chart nodes and visit the Arbiters vendor once unlocked.","Arbitrations open Galvanized Mods, Adaptation and efficient Endo/Vitus progression.","Use the app's live Arbitration card and start with survivable loadouts.",null,300,8,false),
            ("late-steel-path","4 · LATE GAME","Unlock","Unlock and stabilize Steel Path","Enter Steel Path after core mods and weapons are ready; do not treat the unlock itself as proof the build is prepared.","Steel Essence, Incursions and endgame farms depend on it.","Clear daily Incursions with squads while improving one general-purpose loadout.",null,310,8,false),
            ("late-helminth","4 · LATE GAME","System","Unlock Helminth and preserve base frames","Feed only non-Prime Warframes after ranking them and checking whether they are needed for another craft.","Helminth expands build design, but rebuilding accidentally consumed frames is wasted time.","Use Inventory and Mastery records before subsuming or selling a frame.",null,320,8,false),
            ("late-sortie-archon","4 · LATE GAME","Weekly","Build a sustainable weekly loop","Add Sorties, Archon Hunts and unlocked weekly activities according to reward value and available time.","The optimal routine is the one completed consistently, not a bloated checklist that causes burnout.","Choose a 20-minute, 60-minute or completionist routine in profile preferences.",null,330,8,false),
            ("endgame-goal-stack","5 · ENDGAME","Planning","Run goal stacks instead of isolated farms","Choose activities that progress several needs at once: Mastery, standing, resources, Nightwave, relics or market inventory.","Multi-purpose runs reduce grind and decision fatigue.","Create three active goals and ask the advisor for the mission overlap.",null,400,12,false),
            ("endgame-build-validation","5 · ENDGAME","Buildcraft","Validate builds against mission and budget","Treat tier lists as discovery tools. Check patch age, forma cost, enemy level, faction and whether the build depends on arcanes or companions you lack.","Popular builds are often expensive showcases rather than the right build for the current account.","Import community builds, then ask for a budget and final version side by side.",null,410,10,false),
            ("endgame-trading","5 · ENDGAME","Economy","Create a repeatable Platinum loop","Sell duplicate Prime parts, valuable mods or other tradables using current orders rather than trade-chat guesses.","Trading funds slots and convenience without buying bad Market bundles.","Price with live orders, list competitively, and keep progression-critical items.",null,420,5,true),
        };

        _db.Bulk((c, tx) =>
        {
            foreach (var t in tasks)
            {
                using var cmd = c.CreateCommand(); cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO roadmap_tasks(id,phase,category,title,description,why_it_matters,action_hint,source_url,sort_order,minimum_mr,optional)
                    VALUES(@id,@ph,@cat,@title,@d,@why,@hint,@url,@sort,@mr,@opt)
                    ON CONFLICT(id) DO UPDATE SET phase=@ph,category=@cat,title=@title,description=@d,
                      why_it_matters=@why,action_hint=@hint,source_url=@url,sort_order=@sort,minimum_mr=@mr,optional=@opt
                    """;
                cmd.Parameters.AddWithValue("@id", t.Id); cmd.Parameters.AddWithValue("@ph", t.Phase);
                cmd.Parameters.AddWithValue("@cat", t.Category); cmd.Parameters.AddWithValue("@title", t.Title);
                cmd.Parameters.AddWithValue("@d", t.Desc); cmd.Parameters.AddWithValue("@why", t.Why);
                cmd.Parameters.AddWithValue("@hint", t.Hint); cmd.Parameters.AddWithValue("@url", (object?)t.Url ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@sort", t.Order); cmd.Parameters.AddWithValue("@mr", t.Mr);
                cmd.Parameters.AddWithValue("@opt", t.Optional ? 1 : 0); cmd.ExecuteNonQuery();
            }
        });
    }

    private void SeedShopRules()
    {
        var rules = new (string Id,string Keys,string Verdict,int Severity,string Title,string Reason,string Better,string? Url)[]
        {
            ("credits","credits|credit bundle|credit cache","NEVER",100,"Do not buy Credits with Platinum","Credits are an ordinary farmable currency and the Market conversion destroys Platinum value.","Run Credit-focused activities, sell duplicate mods, or ask for the best unlocked Credit farm.",null),
            ("rush","rush|speed up foundry|finish now|rush build","AVOID",95,"Do not rush normal Foundry timers","Rushing pays premium currency to erase a timer while other progression can continue in parallel.","Queue several items, track completion in Foundry, and play a different objective while they build.",OnboardingService.OfficialNewPlayerGuide),
            ("base-frame","base warframe|regular warframe|normal warframe|buy warframe|warframe for platinum","AVOID",90,"Most base Warframes are terrible Platinum value","The Market price mostly skips a farm; many frames have straightforward blueprints or component drops, and Prime variants may trade for less.","Check the acquisition route and current Prime set price first. Buy only as an explicit time-value decision.",null),
            ("base-weapon","base weapon|normal weapon|weapon for platinum|buy weapon","AVOID",88,"Do not buy ordinary weapons for Platinum by default","Many weapon blueprints cost Credits or come from clan research, quests and drops. The included slot and Catalyst rarely justify the markup for a new player.","Buy the blueprint, farm resources, and spend Platinum on capacity instead.",null),
            ("resources","resource bundle|orokin cell|neural sensor|neurode|gallium|plastid|polymer bundle|nano spores|salvage|rubedo","NEVER",92,"Do not buy routine resources","Resource bundles are priced for emergency convenience and are usually replaced by a short targeted farm.","Use the official drop data and ask for a farm matching your unlocked planets.",OfficialDrops),
            ("mod-pack","mod pack|dragon mod pack|eagle mod pack|falcon mod pack|random mod","NEVER",90,"Random mod packs are a trap","You pay Platinum for random outcomes while specific essential mods have known drop sources or player-market prices.","Farm the exact mod or buy that exact mod from another player after checking live orders.",OfficialDrops),
            ("endo","endo bundle|buy endo","NEVER",88,"Do not buy Endo with Platinum","Endo is farmable and the conversion is poor compared with targeted activities and dissolving duplicate mods.","Upgrade only breakpoint ranks early, dissolve duplicates, and unlock better Endo farms over time.",null),
            ("relic-pack","relic pack|relic bundle","AVOID",76,"Do not spend Platinum gambling on relic packs","Random relics do not guarantee the part you need and can include low-value drops.","Earn Syndicate relic packs with standing or buy/farm the exact Prime part or relic.",null),
            ("slots","warframe slot|weapon slot|companion slot|riven slot|loadout slot","BUY",10,"Slots are the best early Platinum purchase","Slots permanently remove inventory friction and let you keep mastered equipment, variants and crafting ingredients.","Buy only the slot type currently blocking you; weapon and Warframe slots come first.",null),
            ("potato","orokin catalyst|orokin reactor","CONSIDER",30,"Catalysts and Reactors are useful, but not automatic buys","They double mod capacity and are valuable on keepers, yet they are also obtainable from events, Nightwave and other rewards.","Use one only after deciding the item is worth keeping and checking free sources.",null),
            ("forma","forma bundle|forma","CONSIDER",25,"Forma bundles can be reasonable later","Forma becomes a recurring buildcraft resource, but early accounts usually need slots and foundational mods more.","Craft Forma from relic blueprints first; buy bundles only when active endgame projects justify the time saved.",null),
            ("cosmetic","skin|syandana|armor set|ephemera|color palette|decor|cosmetic","PERSONAL",5,"Cosmetics are preference, not progression","They provide no combat progress, but a cosmetic you genuinely value is not a bad purchase merely because it is cosmetic.","Protect a progression reserve for slots first, then use a separate fashion budget.",null),
            ("booster","resource booster|affinity booster|credit booster|drop chance booster","CONSIDER",20,"Boosters are situational time savers","A booster can be strong when you have a planned farming block, but weak when purchased without a schedule or during early wandering.","Buy immediately before a focused farm, stack with events when possible, and avoid idle-time waste.",null),
        };
        _db.Bulk((c, tx) =>
        {
            foreach (var r in rules)
            {
                using var cmd=c.CreateCommand(); cmd.Transaction=tx;
                cmd.CommandText="""
                    INSERT INTO shop_rules(id,keywords,verdict,severity,title,reason,better_option,source_url)
                    VALUES(@id,@k,@v,@s,@t,@r,@b,@u)
                    ON CONFLICT(id) DO UPDATE SET keywords=@k,verdict=@v,severity=@s,title=@t,reason=@r,better_option=@b,source_url=@u
                    """;
                cmd.Parameters.AddWithValue("@id",r.Id); cmd.Parameters.AddWithValue("@k",r.Keys);
                cmd.Parameters.AddWithValue("@v",r.Verdict); cmd.Parameters.AddWithValue("@s",r.Severity);
                cmd.Parameters.AddWithValue("@t",r.Title); cmd.Parameters.AddWithValue("@r",r.Reason);
                cmd.Parameters.AddWithValue("@b",r.Better); cmd.Parameters.AddWithValue("@u",(object?)r.Url??DBNull.Value);
                cmd.ExecuteNonQuery();
            }
        });
    }

    private static string? GetString(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
    private static string? Clip(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, max)];
    private static string? SafeHttpUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" ? u.AbsoluteUri : null;
}
