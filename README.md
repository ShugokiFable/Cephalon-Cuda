# Cephalon Cuda 2.2 · Return Protocol

Cephalon Cuda is a Windows companion for new, active, and returning Warframe players. It turns the game’s scattered systems into a practical decision layer: what to do next, what to build, where an item comes from, whether an old build is still worth investing in, and which Platinum purchases are traps.

The app is offline-first. Personal state stays in a local SQLite database, while public game, drop, activity, relic, and market data refresh into a source-stamped knowledge layer for fast retrieval by the advisor.

## What 2.2 adds

### Just Got Back?

The new Return Protocol detects trustworthy local activity from `EE.log`, remembers the previous session gap, and builds a controlled re-entry plan instead of dumping every new system on the player.

It prioritizes:

- ready Foundry claims
- the next unresolved roadmap objective
- the player’s highest-priority goals
- unclaimed redemption codes
- stale data sources that should refresh before decisions
- old builds, Forma, Reactors, Catalysts, Arcanes, and legacy items that need revalidation
- live activities and time-limited opportunities that matter now

Return state is never fabricated from inaccessible account data. The user can confirm play activity manually, edit goals, and control automatic briefings.

### Automatic intelligence pipeline

The background pipeline starts with the app and uses per-source freshness windows so it does not hammer upstream services:

- WFCD item catalog: daily
- official drop data derived from Digital Extremes drop tables: daily
- warframe.market catalog and price snapshot: cached with source-specific freshness rules
- live order metrics: refreshed when a named tradable is relevant to a question
- relic data: every six hours
- Mastery catalog: daily
- world state: maintained by the existing live service
- licensed community build feed: every twelve hours when configured
- personal market listings: refreshed when a market username is configured

Networking uses compression, connection pooling, ETags, Last-Modified, bounded retries, cache preservation, and stale-on-error behavior. A failed refresh never deletes the last valid database.

### Evidence-aware build synthesis

Build questions no longer receive a blind copy of one popularity list. The advisor combines:

- exact item statistics and tags
- imported or licensed community builds
- mod-frequency consensus across matching builds
- average Forma investment
- official acquisition routes for missing components and mods
- current market context when the target is tradable
- the player’s tracked goals, Mastery, inventory, Foundry, and session budget

Answers are instructed to provide a low-investment working stage, upgrade order, Forma stop points, substitutes, assumptions, mission or faction variants, and acquisition routes. Community votes and tiers remain source-stamped opinions rather than truth.

### New-player protection

The Tenno Path workspace includes:

- five-stage progression roadmap
- routine-aware “what should I do now?” planning
- focused acquisition navigator
- Keep / Sell / Ducat / Subsume checks
- build translator and upgrade ladder
- goal stacking
- redemption-code tracking
- deterministic Shop Guardian

Shop Guardian warns against Credits, routine resources, Endo, random mod packs, normal Foundry rushes, base Warframes, base weapons, filler bundles, and other poor-value shortcuts. Slots and deliberate progression purchases are classified separately from cosmetics.

### Personalization and visual system

- Verv, Lotus, Orokin, Entrati, Corpus, Grineer, Void, and High Contrast full-interface palettes
- live visual theme gallery with immediate preview and no restart
- runtime retheming of already-open views, dialogs, overlays, tray icon, custom chrome, and AI Advisor
- custom accent override with automatic contrast correction
- compact or comfortable density applied live
- reduced motion
- UI scaling
- custom Tenno callsign
- configurable startup workspace
- Direct, Coach, Minimal, and Deep-dive advisor behavior
- session-time budget
- automatic-data and return-briefing controls
- configurable refresh cadence
- existing overlay and Immersive Mode options

## Existing workspaces

- Live Overview
- Tenno Path / Return Protocol
- Inventory and disposition checks
- Relic Planner with expected-value ranking and OCR
- Market & Trading with orders, history, ledger, and listing sync
- Mastery Helper
- Foundry timers
- Riven Assistant
- Live Data & Intel health dashboard
- streaming AI Advisor
- overlay HUD, capture, OCR, and Immersive Mode

## Privacy and game safety

Cephalon Cuda does not read game memory, inject code, automate input, or play the game. It uses read-only `EE.log` events, visual capture/OCR, public endpoints, and information entered or confirmed by the user.

Local data, encrypted secrets, caches, settings, and logs are stored under `%LocalAppData%\CephalonCuda`.

## Build the verified release

On 64-bit Windows 10 or Windows 11, run:

```bat
BUILD_V2.2.bat
```

The release pipeline:

1. checks for the .NET 8 SDK and can install it through `winget`
2. restores dependencies
3. builds Release with every warning treated as an error
4. publishes a self-contained single-file `win-x64` executable
5. runs the complete UI `--smoke` cycle
6. runs `--verify-data` against schema v8 and live integrations
7. creates exactly two clean folders under `dist`:
   - `Cephalon Cuda 2.2 - Nexus Release`
   - `Cephalon Cuda 2.2 - Source`
8. creates separate and combined ZIP archives plus SHA-256 hashes

A failed build, smoke test, or live verification stops the pipeline before packaging.

Development shortcut:

```bat
run.bat
```

Publish-only shortcut:

```bat
publish.bat
```

## Diagnostics

```bat
publish\CephalonCuda.exe --smoke
publish\CephalonCuda.exe --verify-data
```

Reports and crash logs are written to `%LocalAppData%\CephalonCuda\logs`.

## Requirements

Runtime:

- 64-bit Windows 10 or Windows 11
- WebView2 Runtime for the embedded advisor panel
- internet access for live data
- OpenRouter API key only for AI features
- Borderless or Windowed Warframe for overlay and capture features

Building from source additionally requires the .NET 8 SDK.

## Community build data boundary

The app supports manual JSON imports and an optional licensed HTTPS feed for community tiers and builds. It does not ship an automated Overframe scraper. See `docs\COMMUNITY_INTEL_SCHEMA.md`.
