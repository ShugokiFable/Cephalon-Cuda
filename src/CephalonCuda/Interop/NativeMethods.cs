using System.Runtime.InteropServices;

namespace CephalonCuda.Interop;

internal static class NativeMethods
{
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TRANSPARENT = 0x00000020; // click-through
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_TOOLWINDOW = 0x00000080;  // hide from alt-tab

    public const int WM_HOTKEY = 0x0312;
    public const uint MOD_NONE = 0x0000;
    public const uint VK_F6 = 0x75;
    public const uint VK_F7 = 0x76;
    public const uint VK_F8 = 0x77;
    public const uint VK_F9 = 0x78;
    public const uint VK_F10 = 0x79;
    public const uint VK_F11 = 0x7A;
    public const uint VK_F12 = 0x7B;

    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    public const int DWMWA_CAPTION_COLOR = 35;       // Win11+: themed title bar
    public const int DWMWA_BORDER_COLOR = 34;

    public const uint LWA_ALPHA = 0x2;

    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004,
                       SWP_NOACTIVATE = 0x0010, SWP_FRAMECHANGED = 0x0020;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll")]
    public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern IntPtr SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    /// <summary>Re-read the frame after an EXSTYLE change; without this some style bits never take effect.</summary>
    private static void FlushStyle(IntPtr hwnd) =>
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);

    public static void MakeClickThrough(IntPtr hwnd)
    {
        var ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        ex |= WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));
        FlushStyle(hwnd);
    }

    /// <summary>
    /// Overlay interactive mode: remove click-through AND no-activate so the HUD can receive
    /// clicks, wheel scrolling and focus. Keep layered + toolwindow. Must flush the frame or
    /// the transparent bit lingers — this was why the old toggle appeared to do nothing.
    /// </summary>
    public static void MakeInteractive(IntPtr hwnd)
    {
        var ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        ex &= ~(long)(WS_EX_TRANSPARENT | WS_EX_NOACTIVATE);
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));
        FlushStyle(hwnd);
    }

    /// <summary>Uniform window alpha (works on normal windows, no WPF AllowsTransparency needed). 255 = opaque.</summary>
    public static void SetWindowAlpha(IntPtr hwnd, byte alpha)
    {
        var ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        if (alpha >= 255)
        {
            // Drop the layered bit entirely so WebView2 renders on the fast path.
            if ((ex & WS_EX_LAYERED) != 0)
            {
                SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex & ~(long)WS_EX_LAYERED));
                FlushStyle(hwnd);
            }
            return;
        }
        if ((ex & WS_EX_LAYERED) == 0)
        {
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_LAYERED));
            FlushStyle(hwnd);
        }
        SetLayeredWindowAttributes(hwnd, 0, alpha, LWA_ALPHA);
    }

    public static void EnableDarkTitleBar(IntPtr hwnd)
    {
        int on = 1;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
    }

    /// <summary>Win11: tint the native title bar to the theme panel color. Silently ignored on Win10.</summary>
    public static void SetCaptionColor(IntPtr hwnd, byte r, byte g, byte b)
    {
        int colorref = r | (g << 8) | (b << 16);
        _ = DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref colorref, sizeof(int));
    }

    /// <summary>Screen-space rectangle of a window's client area.</summary>
    public static System.Drawing.Rectangle? GetClientBounds(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return null;
        if (!GetClientRect(hwnd, out var rc)) return null;
        var origin = new POINT { X = rc.Left, Y = rc.Top };
        if (!ClientToScreen(hwnd, ref origin)) return null;
        int w = rc.Right - rc.Left, h = rc.Bottom - rc.Top;
        if (w <= 0 || h <= 0) return null;
        return new System.Drawing.Rectangle(origin.X, origin.Y, w, h);
    }
}
