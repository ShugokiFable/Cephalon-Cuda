# Tenno Path and Return Protocol

Tenno Path is manual-first because Warframe does not expose a supported public API for full private account progression. The app automates public knowledge and local observations, but never invents quest completion, inventory ownership, or build investment.

## Persistent player state

- `roadmap_tasks`: curated versioned objectives
- `roadmap_progress`: complete, skipped, pinned, timestamp, notes
- `player_goals`: personal priority stack
- `shop_rules`: deterministic purchase guardrails
- `redeem_codes`: imported or user-entered claim tracking
- `player_sessions`: locally observed EE.log/manual play sessions

Curated rows are upserted by stable identifiers. Personal state is not deleted when curated copy changes.

## Return detection

Return Protocol uses:

1. the last trustworthy `EE.log` write/activity timestamp
2. the previous locally observed activity timestamp
3. local session history
4. Foundry, goals, roadmap, codes, source health, and current world state

When Warframe is opened after a long absence, the new log event would normally replace the old timestamp immediately. Return Protocol preserves the pre-return gap until the briefing is acknowledged, preventing a false “current session” result.

Repeated log events within ten minutes update the active session instead of creating duplicate sessions.

## Return briefing output

- days away and confidence-aware headline
- immediate actions
- old-investment checks
- stale-source refresh warnings
- source record counts and freshness
- ready Foundry claims
- next roadmap objective
- top goals
- unclaimed codes

The advisor receives the same compact return summary, not a second disconnected planning system.

## Build synthesis pipeline

For build, mod, Forma, Steel Path, endgame, or loadout queries:

1. resolve the exact target from the local item database
2. retrieve matching community builds from permission-safe imports or a licensed feed
3. calculate mod-frequency consensus and average Forma
4. retrieve official drop/acquisition records for the target and relevant pieces
5. add live market context when tradable
6. combine player state, goals, inventory, Foundry, and time budget
7. require a low-investment stage, upgrade ladder, Forma stop points, substitutes, assumptions, and mission/faction variants

Community rankings are evidence with source, freshness, score, votes, patch, and investment. They are never treated as unquestionable truth.

## Shop Guardian precedence

1. explicit wording rules such as Credits, slots, rushes, or Forma
2. exact/contained match from `game_items`
3. automatic base Warframe or weapon classification
4. Prime/tradable comparison path
5. generic check with local item intelligence

The deterministic verdict provides the guardrail. The advisor performs the exact current comparison using acquisition routes, tracked account state, and live market context.
