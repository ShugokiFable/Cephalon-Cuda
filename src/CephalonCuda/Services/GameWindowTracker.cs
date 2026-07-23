using System.Diagnostics;
using System.Drawing;
using CephalonCuda.Interop;

namespace CephalonCuda.Services;

/// <summary>
/// Tracks the Warframe window (position, size, focus) by process name only — no handles into the
/// game beyond public user32 window queries. Overlay and capture consume <see cref="ClientBounds"/>.
/// </summary>
public sealed class GameWindowTracker : IDisposable
{
    private static readonly string[] ProcessNames = ["Warframe.x64", "Warframe"];

    private CancellationTokenSource? _cts;

    public IntPtr GameHwnd { get; private set; }
    public Rectangle? ClientBounds { get; private set; }
    public bool GameRunning => GameHwnd != IntPtr.Zero;
    public bool GameFocused => GameRunning && NativeMethods.GetForegroundWindow() == GameHwnd;

    public event Action? Changed;

    public void Start()
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => LoopAsync(_cts.Token));
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (true)
        {
            try { if (!await timer.WaitForNextTickAsync(ct)) break; }
            catch (OperationCanceledException) { break; }
            try { Poll(); } catch { /* transient process/window races */ }
        }
    }

    private void Poll()
    {
        var hwnd = FindGameWindow();
        var bounds = hwnd == IntPtr.Zero ? null : NativeMethods.GetClientBounds(hwnd);
        if (hwnd != GameHwnd || bounds != ClientBounds)
        {
            GameHwnd = hwnd;
            ClientBounds = bounds;
            Changed?.Invoke();
        }
    }

    private static IntPtr FindGameWindow()
    {
        foreach (var name in ProcessNames)
        {
            var procs = Process.GetProcessesByName(name);
            try
            {
                foreach (var p in procs)
                    if (p.MainWindowHandle != IntPtr.Zero)
                        return p.MainWindowHandle;
            }
            finally
            {
                foreach (var p in procs) p.Dispose();
            }
        }
        return IntPtr.Zero;
    }

    /// <summary>A short label for an OCR capture: the current node if known, else a generic tag.</summary>
    public string CurrentNodeOrScreen() =>
        App.Services.EeLog.CurrentNode is { Length: > 0 } node && App.Services.EeLog.MissionActive
            ? $"screen @ {node}"
            : GameRunning ? "screen (in game)" : "screen";

    /// <summary>Capture target: game client area when running, else the primary screen.</summary>
    public Rectangle CaptureBounds =>
        ClientBounds ?? System.Windows.Forms.Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
