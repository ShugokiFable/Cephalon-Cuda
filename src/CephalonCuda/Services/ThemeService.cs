using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace CephalonCuda.Services;

public sealed record ThemePalette(
    string Name,
    string Tagline,
    string Description,
    string Bg,
    string Panel,
    string PanelAlt,
    string Border,
    string Accent,
    string AccentDeep,
    string Text,
    string TextDim,
    string AccentAlt,
    string Success,
    string Warn,
    string Danger,
    string Gold);

/// <summary>
/// Runtime theme engine for the full WPF tree and embedded Advisor surface.
///
/// Theme resources are deliberately REPLACED instead of mutating shared Freezables.
/// Every visual brush/effect reference uses DynamicResource, so switching a preset
/// immediately updates already-open views, templates, popups, dialogs, overlays,
/// gradients, custom chrome, and the Advisor WebView without restarting the app.
/// </summary>
public static class ThemeService
{
    public static readonly ThemePalette Verv = new(
        "Verv", "// VERV PROTOCOL", "Carbon-black command deck with high-energy emerald telemetry.",
        "#050806", "#0A100E", "#101A16", "#203329",
        "#00F59A", "#08724D", "#ECFFF7", "#8AA99D",
        "#6EFFE1", "#42E7A5", "#FFC857", "#FF6672", "#FFD76A");

    public static readonly ThemePalette Lotus = new(
        "Lotus", "// DREAMER LINK", "Midnight violet glass with soft orchid and moonlit blue signals.",
        "#070812", "#0E1020", "#171A31", "#34395F",
        "#A99AFF", "#493B9B", "#F7F4FF", "#AAA6C8",
        "#70C7FF", "#72E8C4", "#FFD06A", "#FF7488", "#E8C97A");

    public static readonly ThemePalette Orokin = new(
        "Orokin", "// GILDED ERA", "Obsidian and ivory surfaces edged with restrained Orokin gold.",
        "#0A0907", "#12100D", "#1B1812", "#4A402A",
        "#F3D36E", "#8B6825", "#FFF9E9", "#B7A98A",
        "#FFF0B0", "#7CE0B0", "#F5B94F", "#F06D6D", "#FFD76A");

    public static readonly ThemePalette Entrati = new(
        "Entrati", "// NECRALISK SIGNAL", "Deep biological wine tones with luminous rose and infested amber.",
        "#0B070A", "#151014", "#21161C", "#4A2B3B",
        "#FF7CA6", "#8B2B54", "#FFF0F5", "#B797A3",
        "#FFB06B", "#72D8A6", "#FFC55F", "#FF6677", "#F1C76B");

    public static readonly ThemePalette Corpus = new(
        "Corpus", "// PROFIT CIRCUIT", "Cold navy instrumentation with cyan glass and electric blue highlights.",
        "#040911", "#09131E", "#102235", "#24465E",
        "#47CBFF", "#116987", "#EDF9FF", "#8CADBF",
        "#7E9CFF", "#63E0BA", "#FFC85A", "#FF6D78", "#F4D46A");

    public static readonly ThemePalette Grineer = new(
        "Grineer", "// STEEL CLONE", "Industrial charcoal, oxidized steel, and furnace-orange command lights.",
        "#0A0704", "#15100A", "#21180F", "#4B3522",
        "#FF9B42", "#914716", "#FFF3E7", "#B69A82",
        "#D7C074", "#70D29B", "#FFC857", "#FF665F", "#F5C85C");

    public static readonly ThemePalette Void = new(
        "Void", "// VOID CASCADE", "Near-black teal space with spectral cyan and pale-violet anomalies.",
        "#03080A", "#081216", "#0E1D22", "#27464C",
        "#65F4E8", "#187A72", "#EEFFFD", "#90B8B4",
        "#B49BFF", "#61E3B5", "#FFD168", "#FF7187", "#E9CE72");

    public static readonly ThemePalette HighContrast = new(
        "High Contrast", "// ACCESSIBILITY GRID", "Maximum separation, yellow focus signals, and reduced decorative noise.",
        "#000000", "#070707", "#111111", "#777777",
        "#FFFF00", "#777700", "#FFFFFF", "#D8D8D8",
        "#00FFFF", "#00FF9D", "#FFD000", "#FF4D4D", "#FFFF66");

    public static IReadOnlyList<ThemePalette> All { get; } =
        [Verv, Lotus, Orokin, Entrati, Corpus, Grineer, Void, HighContrast];

    public static ThemePalette Current { get; private set; } = Verv;
    public static Color CurrentAccent { get; private set; } = Parse(Verv.Accent);
    public static Color CurrentBackground { get; private set; } = Parse(Verv.Bg);
    public static Color CurrentSurface { get; private set; } = Parse(Verv.Panel);
    public static bool CompactMode { get; private set; }
    public static bool ReducedMotion { get; private set; }

    public static event Action? ThemeChanged;

    public static ThemePalette Find(string? name) =>
        All.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? Verv;

    public static void RegisterBrushes(ResourceDictionary resources) =>
        WriteThemeResources(resources, Verv, customAccentHex: null, compact: false);

    public static void ApplyFromSettings(SettingsService settings)
    {
        CompactMode = settings.CompactMode;
        ReducedMotion = settings.ReducedMotion;
        Apply(Find(settings.ThemeName), settings.CustomAccent);
    }

    public static void Apply(ThemePalette palette, string? customAccentHex = null)
    {
        Current = palette;
        WriteThemeResources(Application.Current.Resources, palette, customAccentHex, CompactMode);
        ThemeChanged?.Invoke();
    }

    public static bool IsValidHex(string? hex) => TryParse(hex) is not null;

    public static Dictionary<string, string> WebVars()
    {
        var p = Current;
        var accent = CurrentAccent;
        var onAccent = ContrastText(accent);
        string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        string HexA(Color c, byte a) => $"#{c.R:X2}{c.G:X2}{c.B:X2}{a:X2}";

        return new Dictionary<string, string>
        {
            ["bg"] = Hex(CurrentBackground),
            ["panel"] = p.Panel,
            ["panel-alt"] = p.PanelAlt,
            ["border"] = p.Border,
            ["accent"] = Hex(accent),
            ["accent-alt"] = p.AccentAlt,
            ["accent-deep"] = Hex(Blend(accent, Colors.Black, 0.56)),
            ["on-accent"] = Hex(onAccent),
            ["text"] = p.Text,
            ["dim"] = p.TextDim,
            ["danger"] = p.Danger,
            ["gold"] = p.Gold,
            ["glow-strong"] = HexA(accent, 0x66),
            ["glow-soft"] = HexA(accent, 0x38),
            ["glow-faint"] = HexA(accent, 0x1E),
            ["bubble"] = Hex(Blend(accent, Parse(p.Panel), 0.86)),
            ["scroll"] = Hex(Blend(accent, Parse(p.Panel), 0.62)),
            ["motion"] = ReducedMotion ? "0s" : ".2s",
        };
    }

    public static void ApplyCaptionColor(IntPtr hwnd)
    {
        var p = CurrentSurface;
        Interop.NativeMethods.SetCaptionColor(hwnd, p.R, p.G, p.B);
    }

    private static void WriteThemeResources(
        ResourceDictionary resources,
        ThemePalette palette,
        string? customAccentHex,
        bool compact)
    {
        var bg = Parse(palette.Bg);
        var panel = Parse(palette.Panel);
        var panelAlt = Parse(palette.PanelAlt);
        var border = Parse(palette.Border);
        var text = Parse(palette.Text);
        var dim = Parse(palette.TextDim);
        var accent = TryParse(customAccentHex) ?? Parse(palette.Accent);
        var accentAlt = TryParse(customAccentHex) is { } customAccent
            ? Blend(customAccent, Colors.White, 0.28)
            : Parse(palette.AccentAlt);
        var accentDeep = TryParse(customAccentHex) is { } customDeep
            ? Blend(customDeep, Colors.Black, 0.58)
            : Parse(palette.AccentDeep);
        var onAccent = ContrastText(accent);

        CurrentAccent = accent;
        CurrentBackground = bg;
        CurrentSurface = panel;

        // Core surfaces
        SetSolid(resources, "Brush.Bg", bg);
        SetSolid(resources, "Brush.Panel", panel);
        SetSolid(resources, "Brush.PanelAlt", panelAlt);
        SetSolid(resources, "Brush.PanelRaised", Blend(panelAlt, text, 0.055));
        SetSolid(resources, "Brush.Sidebar", Blend(bg, panel, 0.70));
        SetSolid(resources, "Brush.Input", Blend(panelAlt, bg, 0.16));
        SetSolid(resources, "Brush.InputHover", Blend(panelAlt, accent, 0.07));
        SetSolid(resources, "Brush.SurfaceGlass", Color.FromArgb(0xE6, panel.R, panel.G, panel.B));
        SetSolid(resources, "Brush.SurfaceGlassStrong", Color.FromArgb(0xF4, panelAlt.R, panelAlt.G, panelAlt.B));
        SetSolid(resources, "Brush.Code", Blend(bg, panel, 0.35));

        // Borders and interaction states
        SetSolid(resources, "Brush.Border", border);
        SetSolid(resources, "Brush.BorderStrong", Blend(border, accent, 0.34));
        SetSolid(resources, "Brush.Divider", Color.FromArgb(0x78, border.R, border.G, border.B));
        SetSolid(resources, "Brush.Hover", Color.FromArgb(0x1A, accent.R, accent.G, accent.B));
        SetSolid(resources, "Brush.SelectionGlow", Color.FromArgb(0x30, accent.R, accent.G, accent.B));
        SetSolid(resources, "Brush.NavHover", Color.FromArgb(0x18, accent.R, accent.G, accent.B));
        SetSolid(resources, "Brush.NavActive", Color.FromArgb(0x2A, accent.R, accent.G, accent.B));
        SetSolid(resources, "Brush.NavActiveBorder", Color.FromArgb(0x70, accent.R, accent.G, accent.B));
        SetSolid(resources, "Brush.ControlDisabled", Color.FromArgb(0x4C, dim.R, dim.G, dim.B));
        SetSolid(resources, "Brush.WindowButtonHover", Color.FromArgb(0x20, text.R, text.G, text.B));
        SetSolid(resources, "Brush.WindowCloseHover", Parse("#D93B4C"));

        // Accent system
        SetSolid(resources, "Brush.Accent", accent);
        SetSolid(resources, "Brush.AccentAlt", accentAlt);
        SetSolid(resources, "Brush.AccentDeep", accentDeep);
        SetSolid(resources, "Brush.AccentMuted", Color.FromArgb(0x4A, accent.R, accent.G, accent.B));
        SetSolid(resources, "Brush.AccentSoft", Color.FromArgb(0x20, accent.R, accent.G, accent.B));
        SetSolid(resources, "Brush.AccentFaint", Color.FromArgb(0x10, accent.R, accent.G, accent.B));
        SetSolid(resources, "Brush.AccentOverlay", Color.FromArgb(0x70, accent.R, accent.G, accent.B));
        SetSolid(resources, "Brush.OnAccent", onAccent);

        // Typography and semantic signals
        SetSolid(resources, "Brush.Text", text);
        SetSolid(resources, "Brush.TextDim", dim);
        SetSolid(resources, "Brush.TextMuted", Blend(dim, bg, 0.30));
        SetSolid(resources, "Brush.Success", Parse(palette.Success));
        SetSolid(resources, "Brush.Warn", Parse(palette.Warn));
        SetSolid(resources, "Brush.Danger", Parse(palette.Danger));
        SetSolid(resources, "Brush.Gold", Parse(palette.Gold));
        SetSolid(resources, "Brush.StatusIdle", Blend(dim, bg, 0.22));

        // Overlay and scrims
        SetSolid(resources, "Brush.OverlayStrong", Color.FromArgb(0xEC, bg.R, bg.G, bg.B));
        SetSolid(resources, "Brush.OverlayMedium", Color.FromArgb(0xCE, bg.R, bg.G, bg.B));
        SetSolid(resources, "Brush.Scrim", Color.FromArgb(0xB8, bg.R, bg.G, bg.B));

        // Layered atmospheric gradients. Each theme changes both hue and surface balance.
        resources["Brush.WindowBackdrop"] = Gradient(
            Blend(bg, accent, 0.035),
            bg,
            Blend(bg, accentAlt, 0.025),
            new Point(0, 0), new Point(1, 1));
        resources["Brush.SidebarGradient"] = Gradient(
            Blend(panel, accent, 0.045),
            Blend(bg, panel, 0.66),
            Blend(bg, accentAlt, 0.028),
            new Point(0, 0), new Point(0.95, 1));
        resources["Brush.CardGradient"] = Gradient(
            Blend(panelAlt, accent, 0.028),
            panel,
            Blend(panel, bg, 0.16),
            new Point(0, 0), new Point(1, 1));
        resources["Brush.HeroGradient"] = Gradient(
            Color.FromArgb(0x35, accent.R, accent.G, accent.B),
            Blend(panelAlt, accentAlt, 0.055),
            panel,
            new Point(0, 0), new Point(1, 1));
        resources["Brush.AccentGradient"] = Gradient(
            Blend(accent, Colors.White, 0.12),
            accent,
            Blend(accent, accentDeep, 0.48),
            new Point(0, 0), new Point(1, 1));
        resources["Brush.HeaderGradient"] = Gradient(
            Color.FromArgb(0x30, accent.R, accent.G, accent.B),
            Color.FromArgb(0x10, accentAlt.R, accentAlt.G, accentAlt.B),
            Colors.Transparent,
            new Point(0, 0), new Point(1, 0.9));
        resources["Brush.StatusGradient"] = Gradient(
            Blend(panel, accent, 0.025),
            panel,
            Blend(panel, accentAlt, 0.018),
            new Point(0, 0), new Point(1, 0));

        // Density and geometry tokens. All consumers use DynamicResource.
        resources["Metric.CardPadding"] = compact ? new Thickness(14) : new Thickness(20);
        resources["Metric.ControlPadding"] = compact ? new Thickness(12, 6, 12, 6) : new Thickness(15, 8, 15, 8);
        resources["Metric.NavPadding"] = compact ? new Thickness(12, 8, 12, 8) : new Thickness(14, 11, 14, 11);
        resources["Metric.PageMargin"] = compact ? new Thickness(18) : new Thickness(26);
        resources["Metric.CardGap"] = compact ? 9d : 14d;
        resources["Metric.NavWidth"] = new GridLength(compact ? 236d : 264d);
        resources["Metric.TitleBarHeight"] = new GridLength(52d);
        resources["Metric.StatusBarHeight"] = new GridLength(compact ? 32d : 36d);
        resources["Corner.Control"] = new CornerRadius(compact ? 8 : 10);
        resources["Corner.Card"] = new CornerRadius(compact ? 13 : 17);
        resources["Corner.Shell"] = new CornerRadius(compact ? 16 : 22);
        resources["Corner.Pill"] = new CornerRadius(999);

        // Effects are replaced too, avoiding frozen resource failures.
        resources["Fx.Glow"] = new DropShadowEffect
        {
            Color = accent, BlurRadius = 24, ShadowDepth = 0, Opacity = 0.42,
            RenderingBias = RenderingBias.Performance,
        };
        resources["Fx.GlowSoft"] = new DropShadowEffect
        {
            Color = accent, BlurRadius = 14, ShadowDepth = 0, Opacity = 0.25,
            RenderingBias = RenderingBias.Performance,
        };
        resources["Fx.Focus"] = new DropShadowEffect
        {
            Color = accent, BlurRadius = 10, ShadowDepth = 0, Opacity = 0.30,
            RenderingBias = RenderingBias.Performance,
        };
        resources["Fx.Card"] = new DropShadowEffect
        {
            Color = Colors.Black, BlurRadius = compact ? 16 : 24, ShadowDepth = compact ? 3 : 6,
            Direction = 270, Opacity = 0.28, RenderingBias = RenderingBias.Performance,
        };
        resources["Fx.Float"] = new DropShadowEffect
        {
            Color = Colors.Black, BlurRadius = compact ? 22 : 34, ShadowDepth = compact ? 6 : 10,
            Direction = 270, Opacity = 0.42, RenderingBias = RenderingBias.Performance,
        };
    }

    private static void SetSolid(ResourceDictionary resources, string key, Color color) =>
        resources[key] = new SolidColorBrush(color);

    private static LinearGradientBrush Gradient(
        Color start,
        Color middle,
        Color end,
        Point startPoint,
        Point endPoint) => new()
    {
        StartPoint = startPoint,
        EndPoint = endPoint,
        GradientStops = new GradientStopCollection
        {
            new(start, 0),
            new(middle, 0.52),
            new(end, 1),
        },
    };

    private static Color ContrastText(Color c) => RelativeLuminance(c) > 0.48
        ? Parse("#06100C")
        : Colors.White;

    private static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static Color? TryParse(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        var value = hex.Trim();
        if (!value.StartsWith('#')) value = "#" + value;
        if (value.Length != 7) return null;
        try { return Parse(value); }
        catch { return null; }
    }

    private static Color Blend(Color a, Color b, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        byte Mix(byte x, byte y) => (byte)Math.Round(x + (y - x) * amount);
        return Color.FromArgb(Mix(a.A, b.A), Mix(a.R, b.R), Mix(a.G, b.G), Mix(a.B, b.B));
    }

    private static double RelativeLuminance(Color c)
    {
        static double Channel(byte value)
        {
            var x = value / 255d;
            return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }
}
