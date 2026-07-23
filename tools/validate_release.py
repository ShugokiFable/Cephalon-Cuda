#!/usr/bin/env python3
"""Offline release checks for Cephalon Cuda.

This intentionally complements, rather than replaces, `dotnet publish` and the Windows `--smoke`
run. It catches malformed XAML, broken event bindings, duplicate names, unbalanced C# delimiters,
schema/migration failures, missing embedded assets, secrets, and dirty build output.
"""
from __future__ import annotations

import re
import sqlite3
import sys

import yaml
from pathlib import Path
from xml.etree import ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / "src" / "CephalonCuda"
XAML_NS = "{http://schemas.microsoft.com/winfx/2006/xaml}"
EVENT_ATTRS = {
    "Click", "Checked", "Unchecked", "Loaded", "Unloaded", "Closing", "Closed",
    "SelectionChanged", "TextChanged", "ValueChanged", "LostFocus", "GotFocus",
    "MouseDown", "MouseUp", "MouseMove", "MouseEnter", "MouseLeave", "MouseWheel",
    "MouseLeftButtonDown", "MouseLeftButtonUp", "MouseRightButtonDown", "MouseRightButtonUp",
    "PreviewMouseDown", "PreviewMouseUp", "PreviewKeyDown", "PreviewKeyUp", "KeyDown", "KeyUp",
    "DragEnter", "DragLeave", "DragOver", "Drop", "SizeChanged", "StateChanged",
    "SourceInitialized", "ContentRendered", "Navigating", "NavigationCompleted",
}

errors: list[str] = []
notes: list[str] = []


def fail(message: str) -> None:
    errors.append(message)


def check_xaml() -> None:
    count = 0
    handlers = 0
    for path in sorted(PROJECT.rglob("*.xaml")):
        count += 1
        try:
            root = ET.parse(path).getroot()
        except Exception as exc:
            fail(f"Malformed XAML {path.relative_to(ROOT)}: {exc}")
            continue

        names: dict[str, int] = {}
        class_name = root.attrib.get(XAML_NS + "Class")
        code_path = Path(str(path) + ".cs")
        code = code_path.read_text(encoding="utf-8") if code_path.exists() else ""
        for element in root.iter():
            name = element.attrib.get(XAML_NS + "Name") or element.attrib.get("Name")
            if name:
                names[name] = names.get(name, 0) + 1
            for attr, value in element.attrib.items():
                local = attr.rsplit("}", 1)[-1]
                if local not in EVENT_ATTRS or not re.fullmatch(r"[A-Za-z_]\w*", value):
                    continue
                handlers += 1
                if not code_path.exists():
                    fail(f"{path.relative_to(ROOT)} binds {local}={value} but has no code-behind")
                elif not re.search(rf"\b(?:async\s+)?(?:void|Task)\s+{re.escape(value)}\s*\(", code):
                    fail(f"Missing handler {value} for {path.relative_to(ROOT)}")
        root_local = root.tag.rsplit("}", 1)[-1]
        if root_local not in {"ResourceDictionary", "Style", "ControlTemplate", "DataTemplate"}:
            for name, occurrences in names.items():
                if occurrences > 1:
                    fail(f"Duplicate x:Name '{name}' in {path.relative_to(ROOT)}")
        if class_name and code_path.exists():
            short = class_name.rsplit(".", 1)[-1]
            if not re.search(rf"\bpartial\s+class\s+{re.escape(short)}\b", code):
                fail(f"x:Class {class_name} does not match {code_path.relative_to(ROOT)}")
    notes.append(f"XAML: {count} files, {handlers} event bindings")


def strip_csharp_noncode(text: str) -> str:
    """Replace strings/comments with spaces while preserving newlines for delimiter checking."""
    out = list(text)
    i, n = 0, len(text)
    state = "code"
    raw_quotes = 0
    while i < n:
        if state == "code":
            if text.startswith("//", i):
                out[i] = out[i + 1] = " "; i += 2; state = "line"; continue
            if text.startswith("/*", i):
                out[i] = out[i + 1] = " "; i += 2; state = "block"; continue
            # Raw/interpolated raw string, including $""" and $$""".
            m = re.match(r"\$*(\"{3,})", text[i:])
            if m:
                token = m.group(0); raw_quotes = len(m.group(1))
                for j in range(i, i + len(token)): out[j] = " "
                i += len(token); state = "raw"; continue
            if text.startswith('$@"', i) or text.startswith('@$"', i):
                for j in range(i, i + 3): out[j] = " "
                i += 3; state = "verbatim"; continue
            if text.startswith('@"', i):
                out[i] = out[i + 1] = " "; i += 2; state = "verbatim"; continue
            if text.startswith('$"', i):
                out[i] = out[i + 1] = " "; i += 2; state = "string"; continue
            if text[i] == '"':
                out[i] = " "; i += 1; state = "string"; continue
            if text[i] == "'":
                out[i] = " "; i += 1; state = "char"; continue
            i += 1; continue
        if state == "line":
            if text[i] == "\n": state = "code"
            else: out[i] = " "
            i += 1; continue
        if state == "block":
            if text.startswith("*/", i):
                out[i] = out[i + 1] = " "; i += 2; state = "code"
            else:
                if text[i] != "\n": out[i] = " "
                i += 1
            continue
        if state == "raw":
            closer = '"' * raw_quotes
            if text.startswith(closer, i):
                for j in range(i, i + raw_quotes): out[j] = " "
                i += raw_quotes; state = "code"
            else:
                if text[i] != "\n": out[i] = " "
                i += 1
            continue
        if state == "verbatim":
            if text.startswith('""', i):
                out[i] = out[i + 1] = " "; i += 2
            elif text[i] == '"':
                out[i] = " "; i += 1; state = "code"
            else:
                if text[i] != "\n": out[i] = " "
                i += 1
            continue
        if state in {"string", "char"}:
            quote = '"' if state == "string" else "'"
            if text[i] == "\\":
                out[i] = " "
                if i + 1 < n:
                    if text[i + 1] != "\n": out[i + 1] = " "
                    i += 2
                else: i += 1
            elif text[i] == quote:
                out[i] = " "; i += 1; state = "code"
            else:
                if text[i] != "\n": out[i] = " "
                i += 1
    if state not in {"code", "line"}:
        fail(f"Unterminated C# lexical construct ({state})")
    return "".join(out)


def check_csharp() -> None:
    files = sorted(PROJECT.rglob("*.cs"))
    pairs = {"(": ")", "[": "]", "{": "}"}
    closes = {v: k for k, v in pairs.items()}
    for path in files:
        original = path.read_text(encoding="utf-8")
        clean = strip_csharp_noncode(original)
        stack: list[tuple[str, int]] = []
        for lineno, line in enumerate(clean.splitlines(), 1):
            for ch in line:
                if ch in pairs: stack.append((ch, lineno))
                elif ch in closes:
                    if not stack or stack[-1][0] != closes[ch]:
                        fail(f"Delimiter mismatch {ch} in {path.relative_to(ROOT)}:{lineno}")
                        stack.clear(); break
                    stack.pop()
        if stack:
            fail(f"Unclosed delimiter(s) in {path.relative_to(ROOT)}: {stack[-3:]}")
        if "<<<<<<<" in original or ">>>>>>>" in original or re.search(r"^=======$", original, re.M):
            fail(f"Merge marker in {path.relative_to(ROOT)}")

        # Compiler-regression contracts discovered by real Windows warnings-as-errors builds.
        io_tokens = re.search(r"\b(?:File|Directory|Path|FileInfo|FileNotFoundException|InvalidDataException)\b", clean)
        if io_tokens and "using System.IO;" not in original and "System.IO." not in original:
            fail(f"Missing explicit System.IO import in {path.relative_to(ROOT)}")
        if re.search(r"\buri\.Scheme\s+is\s+Uri\.UriScheme", clean):
            fail(f"Nonconstant Uri scheme pattern in {path.relative_to(ROOT)}")

    data_intel = (PROJECT / "Services" / "DataIntelligenceService.cs").read_text(encoding="utf-8")
    if "using System.Net.Http;" not in data_intel:
        fail("DataIntelligenceService.cs is missing using System.Net.Http;")
    onboarding = (PROJECT / "Services" / "OnboardingService.cs").read_text(encoding="utf-8")
    if "string? Url" not in onboarding:
        fail("Onboarding roadmap tuple URL must be nullable")
    return_planner = (PROJECT / "Services" / "ReturnPlannerService.cs").read_text(encoding="utf-8")
    if "_activeSessionId.Value" in return_planner:
        fail("ReturnPlannerService dereferences nullable _activeSessionId.Value")

    notes.append(f"C#: {len(files)} files structurally scanned plus Windows compiler regressions")


def schema_sql() -> str:
    text = (PROJECT / "Services" / "Db.cs").read_text(encoding="utf-8")
    match = re.search(r'cmd\.CommandText\s*=\s*"""\s*(.*?)\s*""";', text, re.S)
    if not match:
        raise RuntimeError("could not extract schema SQL")
    return match.group(1)


def check_schema() -> None:
    try:
        sql = schema_sql()
        db = sqlite3.connect(":memory:")
        db.executescript(sql)
        version = db.execute("PRAGMA user_version").fetchone()[0]
        if version != 8: fail(f"Fresh schema user_version is {version}, expected 8")
        expected = {"official_drops", "player_sessions", "community_builds", "roadmap_progress", "source_state"}
        actual = {r[0] for r in db.execute("SELECT name FROM sqlite_master WHERE type='table'")}
        missing = expected - actual
        if missing: fail(f"Fresh schema missing tables: {sorted(missing)}")
        db.close()

        # Exercise the exact archived V1.8 and V2.1 schemas, not reconstructed approximations.
        fixture_dir = ROOT / "tools" / "migration_fixtures"
        fixtures = {
            "V1.8": fixture_dir / "v1_8_schema.sql",
            "V2.1": fixture_dir / "v2_1_schema.sql",
        }
        for label, fixture in fixtures.items():
            if not fixture.exists():
                fail(f"Missing migration fixture: {fixture.relative_to(ROOT)}")
                continue
            db = sqlite3.connect(":memory:")
            db.executescript(fixture.read_text(encoding="utf-8"))
            db.execute("INSERT INTO settings(key,value) VALUES('migration_sentinel','preserve-me')")
            db.execute("INSERT INTO inventory(name,quantity,notes,updated_at) VALUES('Legacy Item',3,'keep','2026-01-01T00:00:00Z')")
            db.execute("INSERT INTO chat_messages(role,content,created_at) VALUES('user','legacy-chat','2026-01-01T00:00:00Z')")
            db.commit()
            db.executescript(sql)
            if db.execute("SELECT value FROM settings WHERE key='migration_sentinel'").fetchone()[0] != "preserve-me":
                fail(f"{label} -> v8 migration lost settings data")
            if db.execute("SELECT quantity FROM inventory WHERE name='Legacy Item'").fetchone()[0] != 3:
                fail(f"{label} -> v8 migration lost inventory data")
            if db.execute("SELECT content FROM chat_messages WHERE content='legacy-chat'").fetchone()[0] != "legacy-chat":
                fail(f"{label} -> v8 migration lost chat data")
            if db.execute("PRAGMA user_version").fetchone()[0] != 8:
                fail(f"{label} -> v8 migration did not set user_version 8")
            actual = {r[0] for r in db.execute("SELECT name FROM sqlite_master WHERE type='table'")}
            missing = expected - actual
            if missing:
                fail(f"{label} -> v8 migration missing tables: {sorted(missing)}")
            db.close()
        notes.append("SQLite: fresh v8 schema plus archived V1.8 and V2.1 migrations passed")
    except Exception as exc:
        fail(f"SQLite schema validation failed: {exc}")


def check_assets_and_cleanliness() -> None:
    required = [
        PROJECT / "Assets" / "advisor.html",
        PROJECT / "Assets" / "cuda.ico",
        PROJECT / "app.manifest",
        ROOT / "CephalonCuda.sln",
        ROOT / "publish.bat",
        ROOT / "BUILD_V2.2.bat",
        ROOT / "MAKE_FINAL_RELEASE.bat",
        ROOT / "tools" / "Make-Release.ps1",
        ROOT / ".github" / "workflows" / "windows-release.yml",
        ROOT / "NEXUS_README.txt",
        ROOT / "NEXUS_DESCRIPTION_BBCODE.txt",
        ROOT / "PRIVACY_AND_DATA.md",
        ROOT / "THIRD_PARTY_NOTICES.md",
    ]
    for path in required:
        if not path.exists(): fail(f"Missing required file: {path.relative_to(ROOT)}")
    dirty = [p for p in ROOT.rglob("*") if p.is_dir() and p.name.lower() in {"bin", "obj", "publish", ".vs"}]
    if dirty: fail("Dirty build directories present: " + ", ".join(str(p.relative_to(ROOT)) for p in dirty[:10]))
    forbidden_files = [p for p in ROOT.rglob("*") if p.is_file() and p.suffix.lower() in {".db", ".pfx", ".snk"}]
    if forbidden_files: fail("Private/runtime files present: " + ", ".join(str(p.relative_to(ROOT)) for p in forbidden_files))

    secret_patterns = [
        re.compile(r"sk-[A-Za-z0-9_-]{20,}"),
        re.compile(r"(?i)(api[_-]?key|token|password)\s*[:=]\s*[\"'][^\"']{12,}[\"']"),
    ]
    for path in ROOT.rglob("*"):
        if not path.is_file() or path.suffix.lower() not in {".cs", ".xaml", ".md", ".json", ".yml", ".yaml", ".bat", ".txt"}:
            continue
        text = path.read_text(encoding="utf-8", errors="ignore")
        for pattern in secret_patterns:
            if pattern.search(text): fail(f"Possible secret in {path.relative_to(ROOT)}")
    if (ROOT / "BUILD_V2.1.bat").exists():
        fail("Obsolete BUILD_V2.1.bat is still present")

    try:
        workflow = yaml.safe_load((ROOT / ".github" / "workflows" / "windows-release.yml").read_text(encoding="utf-8"))
        if not isinstance(workflow, dict) or "jobs" not in workflow:
            fail("Windows release workflow has no jobs block")
    except Exception as exc:
        fail(f"Windows release workflow is invalid YAML: {exc}")

    notes.append("Assets, secrets, workflow and source-tree cleanliness checked")


def check_feature_contracts() -> None:
    db = (PROJECT / "Services" / "Db.cs").read_text(encoding="utf-8")
    intel = (PROJECT / "Services" / "DataIntelligenceService.cs").read_text(encoding="utf-8")
    returns = (PROJECT / "Services" / "ReturnPlannerService.cs").read_text(encoding="utf-8")
    app_services = (PROJECT / "AppServices.cs").read_text(encoding="utf-8")
    market = (PROJECT / "Services" / "MarketService.cs").read_text(encoding="utf-8")
    settings = (PROJECT / "Services" / "SettingsService.cs").read_text(encoding="utf-8")
    settings_view_cs = (PROJECT / "Views" / "SettingsView.xaml.cs").read_text(encoding="utf-8")
    release_ps1 = (ROOT / "tools" / "Make-Release.ps1").read_text(encoding="utf-8")
    publish_bat = (ROOT / "publish.bat").read_text(encoding="utf-8")
    workflow = (ROOT / ".github" / "workflows" / "windows-release.yml").read_text(encoding="utf-8")
    required_tokens = {
        "explicit HTTP namespace import": (intel, "using System.Net.Http;"),
        "schema v8": (db, "public const int SchemaVersion = 8;"),
        "official drop table": (db, "CREATE TABLE IF NOT EXISTS official_drops"),
        "player sessions": (db, "CREATE TABLE IF NOT EXISTS player_sessions"),
        "official drop sync": (intel, "SyncOfficialDropsCoreAsync"),
        "nested drop inheritance": (intel, "string? inheritedItem"),
        "build synthesis": (intel, "BuildBuildSynthesis"),
        "return briefing": (returns, "ReturnBriefing GetBriefing"),
        "return gap preservation": (returns, "PreviousGameActivityAt"),
        "automatic refresh": (app_services, "RunDataRefreshLoopAsync"),
        "current market item route": (market, 'GetJsonAsync($"/v2/item/{urlName}"'),
        "current market bearer auth": (market, '"Authorization", $"Bearer {jwt}"'),
        "market token normalization": (settings, 'StartsWith("Bearer "'),
        "market profile slug auto-fill": (settings_view_cs, 'me.Slug'),
        "PowerShell RID-aware restore": (release_ps1, 'restore $Project -r win-x64'),
        "PowerShell RID-aware build": (release_ps1, 'build $Project -c Release -r win-x64'),
        "batch RID-aware restore": (publish_bat, 'restore "src\\CephalonCuda\\CephalonCuda.csproj" -r win-x64'),
        "workflow RID-aware restore": (workflow, 'restore src/CephalonCuda/CephalonCuda.csproj -r win-x64'),
        "v2 Ducat metadata fallback": (market, 'el.TryGetProperty("ducats"'),
        "legacy price fallback warning": (market, 'Legacy bulk pricing is unavailable; using cached prices and v2 item metadata.'),
        "optional legacy verification": ((PROJECT / "App.xaml.cs").read_text(encoding="utf-8"), 'CheckOptional("item statistics'),
        "live orders survive chart failure": ((PROJECT / "Views" / "MarketView.xaml.cs").read_text(encoding="utf-8"), 'Historical chart unavailable; live v2 orders are still current.'),
    }
    for label, (text, token) in required_tokens.items():
        if token not in text:
            fail(f"Missing feature contract: {label}")
    notes.append("Return Protocol, official drops, build synthesis and automation contracts checked")



def check_theme_contracts() -> None:
    theme_service = (PROJECT / "Services" / "ThemeService.cs").read_text(encoding="utf-8")
    settings_xaml = (PROJECT / "Views" / "SettingsView.xaml").read_text(encoding="utf-8")
    main_cs = (PROJECT / "Views" / "MainWindow.xaml.cs").read_text(encoding="utf-8")

    dynamic_refs: set[str] = set()
    for path in PROJECT.rglob("*.xaml"):
        text = path.read_text(encoding="utf-8")
        if re.search(r"\{StaticResource (?:Brush|Fx)\.", text):
            fail(f"Theme-breaking StaticResource color/effect token in {path.relative_to(ROOT)}")
        dynamic_refs.update(re.findall(r"\{DynamicResource ((?:Brush|Fx|Metric|Corner)\.[A-Za-z0-9]+)\}", text))

    defined = set(re.findall(r'"((?:Brush|Fx|Metric|Corner)\.[A-Za-z0-9]+)"', theme_service))
    missing = sorted(dynamic_refs - defined)
    if missing:
        fail(f"Theme resources referenced but not registered: {missing}")

    contracts = {
        "resource replacement theme engine": 'resources[key] = new SolidColorBrush(color);',
        "theme gallery": 'x:Name="ThemeGallery"',
        "live theme selection": 'ThemeGallery.SelectedItem is not ThemePalette palette',
        "all-palette runtime smoke": 'foreach (var palette in ThemeService.All)',
        "live visual propagation assertion": 'Theme did not propagate to the live visual tree',
        "Advisor theme bridge": '["accent-alt"]',
    }
    haystacks = {
        "resource replacement theme engine": theme_service,
        "theme gallery": settings_xaml,
        "live theme selection": (PROJECT / "Views" / "SettingsView.xaml.cs").read_text(encoding="utf-8"),
        "all-palette runtime smoke": main_cs,
        "live visual propagation assertion": main_cs,
        "Advisor theme bridge": theme_service,
    }
    for label, token in contracts.items():
        if token not in haystacks[label]:
            fail(f"Missing theme contract: {label}")

    geometry_contracts = {
        "Metric.NavWidth": 'new GridLength',
        "Metric.TitleBarHeight": 'new GridLength',
        "Metric.StatusBarHeight": 'new GridLength',
    }
    for key, marker in geometry_contracts.items():
        line = next((ln for ln in theme_service.splitlines() if f'resources["{key}"]' in ln), "")
        if marker not in line:
            fail(f"Theme geometry token {key} must be a GridLength for DynamicResource use")

    app_cs = (PROJECT / "App.xaml.cs").read_text(encoding="utf-8")
    if 'private bool _smokeMode;' not in app_cs or 'Shutdown(2);' not in app_cs or 'smoke-dispatcher' not in app_cs:
        fail("UI smoke test does not fail closed on dispatcher/XAML exceptions")

    if theme_service.count('public static readonly ThemePalette ') < 8:
        fail("Theme engine exposes fewer than eight complete palettes")
    notes.append(f"Theme engine: {len(dynamic_refs)} live resource tokens, eight presets, runtime propagation and geometry-type smoke")

def check_project_metadata() -> None:
    project = (PROJECT / "CephalonCuda.csproj").read_text(encoding="utf-8")
    for token in ["<UseWPF>true</UseWPF>", "<EnableWindowsTargeting>true</EnableWindowsTargeting>", "<Nullable>enable</Nullable>"]:
        if token not in project: fail(f"Project metadata missing {token}")
    if not re.search(r"<Version>2\.2\.0</Version>", project):
        fail("Project version is not 2.2.0")
    notes.append("Project metadata checked")


def main() -> int:
    check_xaml()
    check_csharp()
    check_schema()
    check_assets_and_cleanliness()
    check_feature_contracts()
    check_theme_contracts()
    check_project_metadata()
    print("Cephalon Cuda offline release validation")
    for note in notes: print(f"[OK]   {note}")
    for error in errors: print(f"[FAIL] {error}")
    print(f"Result: {len(errors)} failure(s)")
    return 1 if errors else 0


if __name__ == "__main__":
    raise SystemExit(main())
