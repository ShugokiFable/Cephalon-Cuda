namespace CephalonCuda.Services;

/// <summary>
/// Compact evergreen guidance indexed for retrieval. Volatile drop rates, rotations, prices and
/// patch-specific builds must come from live sources, never from this bundled text.
/// </summary>
public static class GuideDatabase
{
    public const string WarframeGuide = """

WARFRAME DECISION GUIDE — EVERGREEN CORE
This guide deliberately avoids current rotations, exact drop percentages, prices and patch-specific meta claims.
Use live world-state, official drop data, WFCD and warframe.market for volatile facts.

== FIRST PRINCIPLE: PROGRESS WITHOUT WASTE ==
Warframe rarely explains the opportunity cost of a click. Before spending Platinum, selling an item,
feeding Helminth, choosing a relic reward, investing Forma, or committing a rare upgrade, check:
1. Is the item farmable or is its blueprint sold for Credits?
2. Is a Prime variant cheaper from players than the base Market purchase?
3. Is the item required to craft another item?
4. Has it been mastered already?
5. Does the purchase include permanent capacity, or only skip a temporary farm/timer?
6. Is the advice current for the player's patch and progression stage?

== STARTER PLATINUM ==
Best default use: Warframe slots and weapon slots when capacity blocks crafted gear.
Usually awful value: Credits, routine resources, random mod packs, ordinary base weapons, ordinary base
Warframes, relic gambling, and Foundry rushes. Starter Platinum should be treated as account capacity,
not as a general-purpose currency. Catalysts, Reactors, Forma and boosters can be useful, but only when
attached to a concrete plan and after slot needs are protected.

== JUST GOT BACK INTO THE GAME ==
A return plan should begin with a delta check, not a catalogue of every system added while the player was away.
Refresh live sources, claim ready Foundry items, inspect old investments before spending Forma or rare upgrades,
restore one reliable loadout, then resume the highest-value prerequisite or tracked goal. Separate changes into:
- Matters now because it blocks or improves the player's current goal.
- Useful later after a named prerequisite.
- Safe to ignore for now.
Old builds must be treated as evidence requiring revalidation, especially when they depend on changed abilities,
Arcanes, companions, Incarnon evolutions, Helminth, shards, faction damage or enemy defenses.

== WHAT TO DO NEXT ==
When uncertain, prioritize in this order:
- Active tutorial or main quest prerequisites.
- New Star Chart nodes and Junction requirements.
- A small set of foundational mods and one dependable loadout.
- Inventory capacity and quality-of-life tools such as loot vacuum/radar.
- Mastery from inexpensive blueprints and clan research.
- Standing that would otherwise hit a daily cap.
- Focused farms tied to an explicit goal.
Avoid camping one open world, one resource farm or one build project so long that broader unlocks stall.

== MODDING WITHOUT THE UI CONFUSION ==
A weapon's real power comes from mod interactions and capacity. Build a cheap minimum viable configuration
before copying an expensive showcase. Check whether a community build assumes maxed Primed mods, Arcanes,
Incarnon evolutions, shards, companions, Helminth abilities or faction-specific setup. Do not max every mod
early: partial ranks often deliver most of the benefit for a fraction of the Endo and Credit cost.

== KEEP, SELL, MASTER, SUBSUME ==
Before selling equipment:
- Confirm it reached rank 30 and is recorded as mastered.
- Check whether it is required to craft another weapon.
- Check whether it is difficult, quest-limited or time-gated to reacquire.
- Prefer keeping Prime, event, login, quest and heavily invested equipment.
Before subsuming a Warframe:
- Use only a non-Prime version.
- Master it first.
- Check whether it is required for another craft or is expensive to rebuild.

== ACQUISITION ANSWER FORMAT ==
A useful answer to “where do I get this?” must include:
- Exact item identity and variant.
- Blueprint source and component sources.
- Required planet, node, quest, faction rank, key, resource or activity unlock.
- Whether the item or its parts are tradable.
- Current market alternative when relevant.
- Crafting time and slot requirement.
- Common confusion such as main blueprint versus component blueprint.
If live data is missing, say so instead of inventing a node or drop.

== RELICS AND PRIME REWARDS ==
Judge a relic reward by ownership, rarity, Ducat value, current Platinum value, vault status and whether it
completes a set. The visually rarest option is not always the best choice. Refinement raises odds, not certainty.
Use the live scanner and price data before selecting unfamiliar parts. Do not sell or Ducat a part blindly if it
completes a desired set or has a stronger player-market value.

== FOUNDRY ==
Build several things in parallel. A timer is not a blocker because the account can progress elsewhere.
Routine rushes are poor value. Track completion times and claim when slots permit. Before claiming, confirm
there is enough inventory capacity so the player buys only the needed slot type rather than panic-selling gear.

== GOAL STACKING ==
Efficient play combines objectives. A good plan might advance a Junction, Nightwave act, standing cap,
Mastery target, resource need and relic opening in the same session. The advisor should separate:
- Now: the next concrete mission or menu action.
- Today: a short routine fitting the configured time budget.
- This week: time-gated opportunities worth planning.
- Later: aspirational farms that should not distract from prerequisites.

== BUILD SYNTHESIS PIPELINE ==
A build answer must identify the exact item and intended content, then combine base stats, official acquisition data,
owned or missing pieces, and community samples. Provide four layers:
1. Minimum viable configuration with ordinary or partially ranked mods and no unnecessary Forma.
2. Upgrade ladder showing which replacement produces the next meaningful gain.
3. Final configuration with all external assumptions named.
4. Mission/faction variants only when the swap materially changes performance.
Repeated mods across community builds are consensus signals, not proof. Explain capacity, polarity and opportunity cost,
and stop investment when the player's target content does not justify the next expensive upgrade.

== COMMUNITY TIERS AND BUILDS ==
Tier lists are opinion summaries, not truth. Evaluate patch age, sample size, mission type, enemy level,
investment, ease of use and whether the build depends on unavailable systems. Always provide a budget build
and a final build when the player is new or missing expensive pieces.

== REDEEMING CODES AND CAMPAIGN REWARDS ==
Use the official Warframe redeem page. Codes can be platform- or campaign-limited and may expire.
Track claimed status locally. Never claim an unknown third-party code source is official. For Twitch Drops,
anniversary gifts, alerts and campaigns, verify the current official announcement before promising a reward.

== TRADING ==
Use current player-market orders and recent statistics instead of Trade Chat guesses. Check item rank and
variant. Keep progression-critical items and only sell duplicates when the opportunity cost is understood.
Platinum earned through trade is especially valuable for slots and targeted convenience.

== ADVISOR BEHAVIOR ==
Lead with the recommendation. Then state prerequisites, exact route, cheapest path, fastest reasonable path,
what not to buy or sell, and the next action. Distinguish official/live facts from community opinion. Never hide
uncertainty behind confident wording.

""";
}
