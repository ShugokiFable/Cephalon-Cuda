using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows;
using System.Windows.Threading;
using CephalonCuda.Views;

namespace CephalonCuda.Services;

/// <summary>
/// System tray presence: quick menu, minimize-to-tray, and foundry-ready balloon notifications.
/// The icon is drawn at runtime in the current theme's accent color (WinForms NotifyIcon).
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly MainWindow _window;
    private readonly System.Windows.Forms.NotifyIcon _icon;
    private readonly DispatcherTimer _foundryTimer;
    private readonly HashSet<long> _notifiedJobs = [];

    public TrayService(MainWindow window)
    {
        _window = window;

        _icon = new System.Windows.Forms.NotifyIcon
        {
            Icon = BuildIcon(),
            Text = "Cephalon Cuda",
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => RestoreWindow();

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open Cephalon Cuda", null, (_, _) => RestoreWindow());
        menu.Items.Add("Immersive Mode (F8)", null, (_, _) => _window.Dispatcher.Invoke(_window.ToggleImmersive));
        menu.Items.Add("Toggle HUD Overlay", null, (_, _) => _window.Dispatcher.Invoke(() =>
        {
            var overlay = _window.EnsureOverlay();
            if (overlay.IsVisible) overlay.Hide(); else overlay.Show();
            _window.SyncOverlayButton();
        }));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => _window.Dispatcher.Invoke(() => Application.Current.Shutdown()));
        _icon.ContextMenuStrip = menu;

        ThemeService.ThemeChanged += OnThemeChanged;

        // Minimize-to-tray behavior (opt-in via Settings).
        _window.StateChanged += OnWindowStateChanged;

        // Foundry watchdog: balloon when a build finishes.
        _foundryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _foundryTimer.Tick += (_, _) => CheckFoundry();
        _foundryTimer.Start();
    }

    private void OnThemeChanged()
    {
        var old = _icon.Icon;
        _icon.Icon = BuildIcon();
        old?.Dispose();
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (_window.WindowState == WindowState.Minimized && App.Services.Settings.MinimizeToTray)
        {
            _window.Hide();
            _icon.ShowBalloonTip(1500, "Cephalon Cuda",
                "Still running here — double-click to reopen.", System.Windows.Forms.ToolTipIcon.Info);
        }
    }

    private void RestoreWindow()
    {
        _window.Dispatcher.Invoke(() =>
        {
            _window.Show();
            if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
            _window.Activate();
        });
    }

    private void CheckFoundry()
    {
        if (!App.Services.Settings.FoundryNotifications) return;
        foreach (var job in App.Services.Foundry.GetAll())
        {
            if (!job.IsReady || job.Claimed || !_notifiedJobs.Add(job.Id)) continue;
            _icon.ShowBalloonTip(4000, "⚒ Foundry complete",
                $"{job.Name} is ready to claim, Operator.", System.Windows.Forms.ToolTipIcon.Info);
        }
    }

    /// <summary>Verv diamond drawn in the active accent color; crisp at 16–32px.</summary>
    private static Icon BuildIcon()
    {
        var a = ThemeService.CurrentAccent;
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var bg = ThemeService.CurrentBackground;
            g.Clear(Color.FromArgb(255, bg.R, bg.G, bg.B));

            PointF[] Diamond(float cx, float cy, float r) =>
                [new(cx, cy - r), new(cx + r, cy), new(cx, cy + r), new(cx - r, cy)];

            using var glow = new SolidBrush(Color.FromArgb(70, a.R, a.G, a.B));
            g.FillPolygon(glow, Diamond(16, 16, 14));
            using var pen = new Pen(Color.FromArgb(255, a.R, a.G, a.B), 2.2f);
            g.DrawPolygon(pen, Diamond(16, 16, 11));
            using var core = new SolidBrush(Color.FromArgb(255, a.R, a.G, a.B));
            g.FillPolygon(core, Diamond(16, 16, 5));
        }
        var hicon = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(hicon).Clone(); }
        finally { DestroyIcon(hicon); }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    public void Dispose()
    {
        _foundryTimer.Stop();
        ThemeService.ThemeChanged -= OnThemeChanged;
        _window.StateChanged -= OnWindowStateChanged;
        _icon.Visible = false;
        _icon.Dispose();
    }
}
