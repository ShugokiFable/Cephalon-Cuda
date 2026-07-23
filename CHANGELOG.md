# Cephalon Cuda 2.2

## R3.1 runtime XAML hotfix

- Fixed startup failure caused by `double` theme metrics being assigned through `DynamicResource` to `GridLength` properties.
- `Metric.TitleBarHeight`, `Metric.StatusBarHeight`, and `Metric.NavWidth` now use real `GridLength` values.
- UI smoke verification now fails closed on dispatcher/XAML exceptions instead of showing a dialog and packaging a broken build.
- Added geometry-token runtime and offline regression checks.

# Changelog

## 2.2 Major UI Architecture R3 · 2026-07-23

- Replaced the former brush-mutation theme implementation with a runtime resource replacement engine.
- Converted every application color, gradient, and effect reference to `DynamicResource`, so open views retheme immediately.
- Added eight genuinely distinct full palettes, each with its own surfaces, borders, secondary accent, semantic colors, gradients, and atmosphere.
- Rebuilt the application shell with custom Windows chrome, a true command rail, page context header, layered workspace, and compact telemetry dock.
- Rebuilt the shared WPF control system for modern cards, buttons, inputs, combo boxes, navigation, tabs, tables, lists, scrolling, sliders, progress, focus, hover, and disabled states.
- Replaced the theme combo box with a live visual theme gallery and made preset selection clear any stale custom-accent override.
- Added instant appearance reset, live density updates, themed tray icon backgrounds, and theme-aware Advisor WebView backgrounds.
- Upgraded the Advisor web surface with richer theme atmosphere, glass layers, accent-paired gradients, and improved message/composer hierarchy.
- Added a runtime smoke gate that applies all eight themes to an already-created visual tree and fails if live propagation breaks.
- Expanded offline validation to reject theme-breaking `StaticResource` color/effect tokens and missing runtime resources.

## 2.2 compiler corrections R2 · 2026-07-23

- Resolved all 50 diagnostics from the second Windows warnings-as-errors build.
- Added explicit `System.IO` imports to data, market, and onboarding services.
- Replaced invalid `Uri.UriScheme*` constant patterns with explicit scheme comparisons.
- Removed a nullable session-ID dereference in return-player tracking.
- Corrected onboarding roadmap URL tuple nullability.
- Expanded offline compiler-regression contracts for every discovered root cause.

## 2.2 build hotfix · 2026-07-23

- Added the missing `System.Net.Http` import required by `DataIntelligenceService`.
- Added an offline regression contract so the release validator rejects this exact compile blocker in future packages.


## 2.2.0 · Return Protocol

### Final interface polish

- Rebuilt the application shell as a layered, theme-aware command center with a floating navigation rail, cleaner workspace framing, grouped navigation, compact status dock, and higher-contrast hierarchy.
- Reworked all eight themes with coordinated gradients, raised surfaces, contextual borders, accessible on-accent text, themed overlays, and consistent hover, focus, selected, disabled, and error states.
- Modernized typography, spacing, cards, buttons, inputs, checkboxes, combo boxes, tabs, tables, scrollbars, sliders, progress bars, tooltips, and list selection throughout every workspace.
- Added reduced-motion-aware workspace transitions and polished the Immersive Mode and overlay controls without changing their keyboard or docking behavior.
- Rebuilt the Live Overview, Tenno Path, Live Data & Intel, Market, Inventory, Foundry, Relic, Mastery, Riven, Overlay, Settings, and Advisor headers around clearer user goals rather than developer-style labels.
- Reworked the AI Advisor into a responsive, high-end conversation surface with a guided welcome state, data-signal badges, quick-action rail, improved message hierarchy, better code blocks, and a floating composer.
- Removed remaining theme-breaking hard-coded interface colors so custom accents and every included palette apply consistently to charts and overlays.

### Final verification corrections

- Prefer the canonical warframe.market profile slug returned by `/v2/me` when auto-configuring public-order sync.

- Corrected the current warframe.market item-details route from the invalid `/v2/items/{slug}` path to `/v2/item/{slug}`.
- Updated authenticated warframe.market v2 requests to use `Authorization: Bearer`, while accepting pasted `Bearer` or legacy `JWT` prefixes.
- Added item-detail route coverage to the executable live-data verifier.
- Made the Riven verification tolerant of a valid empty auction result instead of throwing on an empty `Min()` sequence.
- Added offline contract checks that prevent the route and authorization scheme from regressing.
- Made restore, build, and publish consistently target `win-x64`, preventing `--no-restore` publish failures from a missing runtime target graph.
- Seeded Ducat metadata from the documented v2 item model so a retired legacy bulk endpoint cannot leave the local economy database empty.
- Made legacy bulk prices, item statistics, and Riven auctions optional enrichments with cached or v2 live-order fallbacks.
- Isolated historical-chart failures so current item details and live order books still load normally.
- Added a 30-minute retry cooldown and honest degraded-data messaging instead of repeated failed network bursts or false “prices refreshed” claims.

### Returning-player command center

- Added the **Just Got Back?** workspace and automatic return briefings.
- Added trustworthy last-played tracking from local `EE.log` activity and manual confirmation.
- Preserved the pre-return gap after Warframe launches so the briefing does not collapse into “played today.”
- Added deduplicated local session history with player, node, duration, and mission counts.
- Added prioritized return actions, investment checks, and source-readiness status.
- Added one-click prompts for old-build audits, meaningful-change summaries, and time-boxed return sessions.

### Automatic data engine

- Added official drop intelligence from WFCD’s flattened Digital Extremes drop dataset.
- Added conditional ETag and Last-Modified refreshes, source TTLs, response-size guards, transactional replacement, and stale-cache preservation.
- Added background refresh controls and a configurable 15, 30, 60, or 120 minute scheduler.
- Added automatic refreshes for items, official drops, market snapshots, relics, Mastery, configured community feeds, and personal market listings.
- Added live named-item market priming before advisor retrieval.
- Bumped the additive SQLite schema from v7 to v8 with `official_drops` and `player_sessions`.

### Build synthesis

- Added query-triggered build synthesis for Warframes, weapons, Steel Path, endgame, Forma, mods, and loadouts.
- Added community-build consensus across matching imports, including mod frequency and average Forma.
- Added official acquisition evidence and market context to build answers.
- Added required low-investment stages, upgrade order, Forma stop points, substitutes, assumptions, and mission/faction variants.
- Improved nested official-drop parsing so parent item names inherit detailed enemy, mission, rotation, and chance sources.

### User experience and reliability

- Added first-run and return-player routing without inventing inaccessible account state.
- Added automation controls to Settings and live source status to Return Protocol.
- Updated branding to **Cephalon Cuda 2.2 · Return Protocol**.
- Added an offline release validator for XAML, event bindings, C# structure, schema v8, v7 migration, assets, secrets, and dirty build output.
- Added a strict Windows release pipeline that treats warnings as errors, smoke-tests every workspace, verifies live integrations, and only then packages the Nexus and source folders.
- Added a GitHub Actions Windows release workflow.

## 2.1.0 · Tenno OS

- Added Tenno Path, roadmap state, goal stacking, acquisition and disposition workflows, Shop Guardian, redeem tracking, themes, advisor styles, UI density, and startup personalization.
- Added deterministic warnings for poor-value Platinum purchases and database-assisted item classification.
- Bumped the additive SQLite schema from v6 to v7.

## 2.0.0 · Live Intel

- Added migration-safe intelligence tables, source health, persistent HTTP caching, live market metrics, community tiers/builds, and AI knowledge indexes.
- Added WFCD item synchronization and improved warframe.market catalog, snapshot, order, retry, pacing, and stale-cache behavior.
- Replaced giant prompt dumps with query-specific FTS retrieval.
- Added permission-safe community imports and optional licensed HTTPS feeds.
