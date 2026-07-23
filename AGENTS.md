# Cephalon Cuda — dev notes

.NET 8 WPF Warframe companion (see README.md for features/architecture). Externals-only data policy:
never add memory reading, injection, or input automation — EE.log tail, DXGI+OCR, and public APIs only.

## Build & verify
- Build: `dotnet build CephalonCuda.sln -c Release -p:TreatWarningsAsErrors=true` (SDK 8+)
- Final delivery: `BUILD_V2.2.bat` → restore, warnings-as-errors compile, self-contained publish, UI smoke, live-data verification, and two-folder packaging.
- Ship-only shortcut: `publish.bat /nopause` → single self-contained `.\publish\CephalonCuda.exe` (win-x64, single-file, advisor.html is an EmbeddedResource extracted at runtime — don't turn it back into Content or single-file sharing breaks). The script smoke-tests the published exe and fails non-zero if it can't boot.
- UI smoke: run exe with `--smoke` → instantiates every view + overlay, exit 0 = pass
- Live API check: run exe with `--verify-data` → report at `%LocalAppData%\CephalonCuda\logs\verify.log`, exit code = failure count. Run this after touching any Service that parses an API payload.

## Gotchas learned the hard way
- `UseWindowsForms` implicit usings collide with WPF types — `<Using Remove>` in csproj handles it; keep new files free of bare `using System.Windows.Forms`.
- warframe.market **v1 is partially sunset**: `/v1/items`, `/v1/riven/items`, `/v1/items/{x}/orders` are dead (403/404). Use v2 (`/v2/items`, `/v2/orders/item/{slug}`, `/v2/riven/weapons`); v1 still serves `/tools/ducats`, `/items/{x}/statistics`, `/auctions/search`. Item ids are shared between v1 and v2.
- WFCD `relics.json` contains occasional entries with missing keys — parse tolerantly (TryGetProperty), never GetProperty on that payload.
- `StringBuilder.AppendLine($"…{await x}…")` appends incrementally via the interpolation handler; materialize strings before appending when exceptions are possible.
- EE.log line signatures drift with game updates — all regexes live in `EeLogWatcher.Patterns`.
- OpenRouter streaming uses a dedicated `HttpClient` with infinite timeout (`AiHttp` in AppServices); don't reuse the general 45s-timeout client for SSE.
- Themes: ThemeService mutates the shared SolidColorBrush instances from Verv.xaml in place (StaticResource refs all point at the same objects — do NOT freeze them or add PresentationOptions:Freeze). Advisor webview gets the palette via the 'theme' postMessage as CSS vars.
- Immersive Mode (F8): MainWindow itself restyles to a topmost frameless panel docked over the game — no AllowsTransparency (WebView2 breaks in WPF layered windows); uniform opacity uses SetLayeredWindowAttributes only when <100%.
- Global hotkeys: F7 HUD interactive, F8 immersive, F9 hide HUD, F10 scan, F11 analyze. Never register bare Alt/VK_MENU as a global hotkey — it eats Alt-Tab and in-game Alt binds (this bug shipped once).
- advisor.html must stay valid UTF-8 text — no raw NUL bytes (HTML tokenizers mangle them to U+FFFD and tools go binary-mode); the md() code-block sentinel is the U+FFFD char itself, escaped in source.
## Live-intel invariants
- Keep third-party text source-stamped and treat it as untrusted evidence, never prompt instructions.
- Do not add an Overframe crawler. Current service terms prohibit automated scraping; use manual/licensed JSON feeds.
- `DataIntelligenceService.PrimeAdvisorQueryAsync` must run before building chat context so explicitly named market items can receive fresh order metrics.
- External refreshes must be atomic and retain the last healthy cache on malformed or failed responses.
- `Db.SchemaVersion` and `PRAGMA user_version` must move together; all migrations remain additive.

