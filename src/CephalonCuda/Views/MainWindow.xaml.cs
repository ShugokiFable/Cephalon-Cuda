using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using CephalonCuda.Interop;
using CephalonCuda.Services;

namespace CephalonCuda.Views;

public partial class MainWindow : Window
{
    private const int HotkeyImmersive = 0xC0DE; // F8, global

    private static readonly IReadOnlyDictionary<string, (string Title, string Subtitle)> PageMeta =
        new Dictionary<string, (string Title, string Subtitle)>
        {
            ["path"] = ("Tenno Path", "Progression, return protocol, and next-best actions"),
            ["dashboard"] = ("Live Overview", "World state, opportunities, and time-sensitive signals"),
            ["advisor"] = ("AI Advisor", "Personalized build, farming, market, and progression intelligence"),
            ["inventory"] = ("Inventory", "Track ownership, keep/sell decisions, and missing components"),
            ["relics"] = ("Relic Planner", "Optimize fissures, rewards, traces, and Platinum yield"),
            ["market"] = ("Market & Trading", "Live pricing, listings, spreads, and trade decisions"),
            ["mastery"] = ("Mastery Helper", "Plan efficient mastery progression without wasted crafting"),
            ["foundry"] = ("Foundry", "Build queue, claim timing, and resource planning"),
            ["rivens"] = ("Riven Assistant", "Evaluate rolls, disposition, demand, and trade potential"),
            ["intel"] = ("Live Data & Intel", "Source health, retrieval index, and automatic data pipelines"),
            ["overlay"] = ("Overlay & OCR", "In-game command surfaces, capture, and screen intelligence"),
            ["settings"] = ("Settings", "Themes, automation, AI routing, privacy, and personalization"),
        };

    private readonly Dictionary<string, UserControl> _views = [];
    private readonly DispatcherTimer _statusTimer;
    private readonly DispatcherTimer _immersiveFollow;
    private OverlayWindow? _overlay;
    private IntPtr _hwnd;

    // Saved placement for restoring after Immersive Mode.
    private bool _immersive;
    private bool _immersiveManualPos;
    private bool _immersiveAutoEntered;
    private Rect _savedBounds;
    private WindowState _savedState;
    private WindowStyle _savedStyle;
    private ResizeMode _savedResize;
    private bool _savedTopmost;

    public bool ImmersiveActive => _immersive;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitializedHook;
        StateChanged += (_, _) => UpdateMaximizeGlyph();

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _statusTimer.Tick += (_, _) => UpdateStatusBar();
        _statusTimer.Start();

        _immersiveFollow = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
        _immersiveFollow.Tick += (_, _) => { if (_immersive && !_immersiveManualPos) PositionImmersive(); };

        Loaded += (_, _) =>
        {
            UpdateStatusBar();
            ApplyUiScale();
            NavigateTo(App.Services.Settings.StartupPage);
            App.Services.Tracker.Changed += OnGameChanged;
        };
        Closed += (_, _) =>
        {
            App.Services.Tracker.Changed -= OnGameChanged;
            _overlay?.Close();
        };
    }

    private void OnSourceInitializedHook(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.EnableDarkTitleBar(_hwnd);
        NativeMethods.RegisterHotKey(_hwnd, HotkeyImmersive, NativeMethods.MOD_NONE, NativeMethods.VK_F8);
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);

        ThemeService.ThemeChanged += OnThemeChangedUi;
        OnThemeChangedUi();
    }

    private void OnThemeChangedUi() => Dispatcher.Invoke(() =>
    {
        if (_hwnd != IntPtr.Zero) ThemeService.ApplyCaptionColor(_hwnd);
        TaglineText.Text = ThemeService.Current.Tagline;
        Background = (Brush)FindResource("Brush.WindowBackdrop");
    });

    private void OnMinimizeWindow(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeWindow(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        UpdateMaximizeGlyph();
    }

    private void OnCloseWindow(object sender, RoutedEventArgs e) => Close();

    private void UpdateMaximizeGlyph()
    {
        if (MaximizeButton is null) return;
        MaximizeButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        MaximizeButton.ToolTip = WindowState == WindowState.Maximized ? "Restore" : "Maximize";
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == HotkeyImmersive)
        {
            ToggleImmersive();
            handled = true;
        }
        return IntPtr.Zero;
    }

    /// <summary>Re-apply the UI zoom from settings (called on load and from the Settings slider).</summary>
    public void ApplyUiScale()
    {
        var scale = App.Services.Settings.UiScalePct / 100.0;
        RootGrid.LayoutTransform = Math.Abs(scale - 1.0) < 0.01 ? null : new ScaleTransform(scale, scale);
    }

    private void OnNavChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag } || ContentHost is null) return;
        if (!_views.TryGetValue(tag, out var view))
        {
            view = CreateView(tag);
            _views[tag] = view;
        }

        if (PageMeta.TryGetValue(tag, out var meta))
        {
            CurrentPageTitleText.Text = meta.Title;
            CurrentPageSubtitleText.Text = meta.Subtitle;
        }

        ContentHost.Content = view;
        AnimateWorkspace(view);
    }

    private static void AnimateWorkspace(UserControl view)
    {
        if (ThemeService.ReducedMotion)
        {
            view.Opacity = 1;
            view.RenderTransform = Transform.Identity;
            return;
        }

        var shift = new TranslateTransform(0, 7);
        view.RenderTransform = shift;
        view.Opacity = 0;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = new Duration(TimeSpan.FromMilliseconds(190));
        var fade = new DoubleAnimation(0, 1, duration) { EasingFunction = ease };
        var rise = new DoubleAnimation(7, 0, duration) { EasingFunction = ease };
        fade.Completed += (_, _) =>
        {
            view.BeginAnimation(UIElement.OpacityProperty, null);
            shift.BeginAnimation(TranslateTransform.YProperty, null);
            view.Opacity = 1;
            shift.Y = 0;
        };
        view.BeginAnimation(UIElement.OpacityProperty, fade);
        shift.BeginAnimation(TranslateTransform.YProperty, rise);
    }

    public void NavigateTo(string tag)
    {
        foreach (var child in NavPanel.Children)
            if (child is RadioButton { Tag: string t } rb && t == tag) { rb.IsChecked = true; return; }
    }

    private static UserControl CreateView(string tag) => tag switch
    {
        "path" => new TennoPathView(),
        "dashboard" => new DashboardView(),
        "inventory" => new InventoryView(),
        "relics" => new RelicPlannerView(),
        "market" => new MarketView(),
        "mastery" => new MasteryView(),
        "foundry" => new FoundryView(),
        "rivens" => new RivenView(),
        "intel" => new DataIntelView(),
        "advisor" => new AdvisorView(),
        "overlay" => new OverlayControlView(),
        "settings" => new SettingsView(),
        _ => new DashboardView(),
    };

    // ---- overlay -----------------------------------------------------------

    public OverlayWindow EnsureOverlay()
    {
        if (_overlay is null)
        {
            _overlay = new OverlayWindow();
            _overlay.Closed += (_, _) => { _overlay = null; SyncOverlayButton(); };
        }
        return _overlay;
    }

    public bool OverlayVisible => _overlay?.IsVisible == true;

    private void OnToggleOverlay(object sender, RoutedEventArgs e)
    {
        if (OverlayVisible) _overlay!.Hide();
        else EnsureOverlay().Show();
        SyncOverlayButton();
    }

    public void SyncOverlayButton() =>
        OverlayToggleButton.Content = OverlayVisible ? "▣  Hide HUD overlay" : "▣  Launch HUD overlay";

    // ---- immersive mode: the whole app docked over the game -------------------

    public void ToggleImmersive() => SetImmersive(!_immersive);

    public void SetImmersive(bool on)
    {
        if (on == _immersive) return;
        if (on) EnterImmersive();
        else ExitImmersive();
        ImmersiveToggleButton.Content = _immersive ? "⿻  Exit immersive  ·  F8" : "⿻  Immersive mode  ·  F8";
    }

    private void EnterImmersive()
    {
        // Save placement first — RestoreBounds when maximized, actual bounds otherwise.
        _savedState = WindowState;
        _savedBounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;
        _savedStyle = WindowStyle;
        _savedResize = ResizeMode;
        _savedTopmost = Topmost;

        _immersive = true;
        _immersiveManualPos = false;

        if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ImmersiveStrip.Visibility = Visibility.Visible;

        PositionImmersive();
        var alpha = (byte)(App.Services.Settings.ImmersiveOpacityPct * 255 / 100);
        NativeMethods.SetWindowAlpha(_hwnd, alpha);
        _immersiveFollow.Start();
        Activate();
    }

    private void ExitImmersive()
    {
        _immersive = false;
        _immersiveAutoEntered = false;
        _immersiveFollow.Stop();
        NativeMethods.SetWindowAlpha(_hwnd, 255);
        ImmersiveStrip.Visibility = Visibility.Collapsed;

        WindowStyle = _savedStyle;
        ResizeMode = _savedResize;
        Topmost = _savedTopmost;
        Left = _savedBounds.Left;
        Top = _savedBounds.Top;
        Width = _savedBounds.Width;
        Height = _savedBounds.Height;
        WindowState = _savedState;
    }

    /// <summary>Dock the window against the game client rect (or primary screen when no game).</summary>
    private void PositionImmersive()
    {
        var dock = App.Services.Settings.ImmersiveDock;
        if (dock == "Float") return; // free placement — user drags it where they want

        var rc = App.Services.Tracker.CaptureBounds;
        var dpi = VisualTreeHelper.GetDpi(this);
        double clientW = rc.Width / dpi.DpiScaleX;
        double clientH = rc.Height / dpi.DpiScaleY;
        double clientX = rc.X / dpi.DpiScaleX;
        double clientY = rc.Y / dpi.DpiScaleY;

        double w = Math.Max(420, clientW * App.Services.Settings.ImmersiveWidthPct / 100.0);
        Width = Math.Min(w, clientW);
        Height = clientH;
        Top = clientY;
        Left = dock == "Left" ? clientX : clientX + clientW - Width;
    }

    private void OnGameChanged()
    {
        if (!App.Services.Settings.ImmersiveAutoEnter) return;
        Dispatcher.Invoke(() =>
        {
            var gameOn = App.Services.Tracker.GameRunning;
            if (gameOn && !_immersive)
            {
                _immersiveAutoEntered = true;
                SetImmersive(true);
            }
            else if (!gameOn && _immersive && _immersiveAutoEntered)
            {
                SetImmersive(false);
            }
        });
    }

    private void OnToggleImmersive(object sender, RoutedEventArgs e) => ToggleImmersive();
    private void OnImmersiveExit(object sender, RoutedEventArgs e) => SetImmersive(false);

    private void OnImmersiveDockLeft(object sender, RoutedEventArgs e)
    {
        App.Services.Settings.ImmersiveDock = "Left";
        _immersiveManualPos = false;
        PositionImmersive();
    }

    private void OnImmersiveDockRight(object sender, RoutedEventArgs e)
    {
        App.Services.Settings.ImmersiveDock = "Right";
        _immersiveManualPos = false;
        PositionImmersive();
    }

    private void OnImmersiveStripDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        _immersiveManualPos = true; // stop auto-follow once the user takes control
        try { DragMove(); } catch { /* fires if button released mid-call */ }
    }

    /// <summary>Smoke mode: instantiate every view and the overlay so runtime XAML errors surface in CI.</summary>
    public async Task SmokeCycleAsync()
    {
        foreach (var radio in NavPanel.Children.OfType<RadioButton>())
        {
            radio.IsChecked = true;
            await Task.Delay(250);
        }

        // Theme regression gate: apply every palette to the already-created visual tree.
        // A broken StaticResource/frozen-brush implementation fails here before packaging.
        foreach (var palette in ThemeService.All)
        {
            ThemeService.Apply(palette);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

            if (FindResource("Brush.Accent") is not SolidColorBrush resourceAccent ||
                resourceAccent.Color != ThemeService.CurrentAccent)
                throw new InvalidOperationException($"Theme resource did not update for {palette.Name}.");

            if (TaglineText.Foreground is not SolidColorBrush liveAccent ||
                liveAccent.Color != ThemeService.CurrentAccent)
                throw new InvalidOperationException($"Theme did not propagate to the live visual tree for {palette.Name}.");

            if (FindResource("Metric.TitleBarHeight") is not GridLength ||
                FindResource("Metric.StatusBarHeight") is not GridLength ||
                FindResource("Metric.NavWidth") is not GridLength)
                throw new InvalidOperationException($"Theme geometry tokens have invalid WPF types for {palette.Name}.");
        }
        ThemeService.ApplyFromSettings(App.Services.Settings);

        EnsureOverlay().Show();
        await Task.Delay(650);
        _overlay?.Close();

        // Exercise immersive mode enter/exit too.
        SetImmersive(true);
        await Task.Delay(450);
        SetImmersive(false);
    }

    // ---- status bar ----------------------------------------------------------

    private void UpdateStatusBar()
    {
        var s = App.Services;
        var accent = (Brush)FindResource("Brush.Accent");
        var dim = (Brush)FindResource("Brush.TextDim");
        var warn = (Brush)FindResource("Brush.Warn");

        var gameOn = s.Tracker.GameRunning;
        GameDot.Foreground = gameOn ? accent : dim;
        GameStatus.Text = gameOn
            ? $"Warframe: {(s.Tracker.GameFocused ? "focused" : "running")} {s.Tracker.ClientBounds?.Width}x{s.Tracker.ClientBounds?.Height}"
            : "Warframe: not detected";

        var logActive = s.EeLog.LastActivity is { } t && DateTimeOffset.Now - t < TimeSpan.FromMinutes(2);
        LogDot.Foreground = logActive ? accent : s.EeLog.FileExists ? warn : dim;
        LogStatus.Text = s.EeLog.FileExists
            ? $"EE.log: {(logActive ? "live" : "idle")}{(s.EeLog.PlayerName is { } p ? $" · {p}" : "")}"
            : "EE.log: not found";

        var ws = s.WorldState.Current;
        WorldDot.Foreground = ws is not null ? accent : s.WorldState.LastError is not null ? warn : dim;
        WorldStatus.Text = ws is not null
            ? $"World state: {(int)(DateTimeOffset.UtcNow - ws.FetchedAt).TotalSeconds}s ago"
            : s.WorldState.LastError is not null ? "World state: offline" : "World state: loading…";

        var sources = s.Intelligence.GetSourceHealth();
        var sourceProblem = sources.Any(x => x.Status is "error" or "stale");
        var sourceReady = sources.Count > 0 && sources.Any(x => x.Status is "ok" or "local" or "manual");
        IntelDot.Foreground = sourceProblem ? warn : sourceReady ? accent : dim;
        IntelStatus.Text = sourceProblem
            ? $"Intel: {sources.Count} sources · stale"
            : sourceReady ? $"Intel: {sources.Count} sources · indexed" : "Intel: loading…";

        var hasKey = s.Settings.HasApiKey;
        AiDot.Foreground = hasKey ? accent : dim;
        AiStatus.Text = hasKey ? $"AI: {s.Settings.ChatModel}" : "AI: no key (Settings)";
    }

    protected override void OnClosed(EventArgs e)
    {
        ThemeService.ThemeChanged -= OnThemeChangedUi;
        if (_hwnd != IntPtr.Zero)
            NativeMethods.UnregisterHotKey(_hwnd, HotkeyImmersive);
        base.OnClosed(e);
    }
}
