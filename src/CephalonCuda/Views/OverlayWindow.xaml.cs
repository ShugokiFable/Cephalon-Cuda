using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CephalonCuda.Interop;
using CephalonCuda.Models;
using CephalonCuda.Services;

namespace CephalonCuda.Views;

/// <summary>
/// Transparent, always-on-top, fully click-through overlay that follows the Warframe client area.
/// F10 scans the relic reward screen on demand; EE.log detection triggers scans automatically.
/// F11 analyzes the current screen with the vision model. F7 makes the HUD mouse-interactive so
/// the user can type in the chat panel; F12 toggles the chat panel itself.
/// Requires Borderless/Windowed game mode (exclusive fullscreen hides overlays by design).
/// </summary>
public partial class OverlayWindow : Window
{
    private const int HotkeyHide = 0xC0DA;
    private const int HotkeyScan = 0xC0DB;
    private const int HotkeyAnalyze = 0xC0DC;
    private const int HotkeyInteractive = 0xC0DD;
    private const int HotkeyChat = 0xC0DE;
    private const int HotkeyLock = 0xC0DF;

    private readonly DispatcherTimer _followTimer;
    private bool _panelsLocked = true;
    private UIElement? _dragTarget;
    private TranslateTransform? _dragTransform;
    private Point _dragStart;
    private Point _dragOrigin;
    private readonly DispatcherTimer _hideRewardsTimer;
    private readonly DispatcherTimer _toastTimer;
    private readonly ObservableCollection<ChatBubble> _chatBubbles = [];
    private IntPtr _hwnd;
    private bool _panelsVisible = true;
    private bool _interactiveMode;
    private CancellationTokenSource? _chatCts;
    private bool _chatBusy;
    private bool _chatHistoryLoaded;

    public sealed record RewardCard(string MatchedName, string PlatDisplay, string DucatDisplay, Visibility BestVisibility);

    public OverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;

        _followTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _followTimer.Tick += (_, _) => FollowGameWindow();
        _followTimer.Start();

        _hideRewardsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(25) };
        _hideRewardsTimer.Tick += (_, _) => { RewardPanel.Visibility = Visibility.Collapsed; _hideRewardsTimer.Stop(); };

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); Toast.Visibility = Visibility.Collapsed; };

        App.Services.Scanner.ScanCompleted += hits => Dispatcher.Invoke(() => ShowRewards(hits));
        App.Services.Scanner.ScanFailed += msg => Dispatcher.Invoke(() => ShowToast($"Scan failed: {msg}"));
        App.Services.EeLog.RelicRewardScreen += OnRelicScreenDetected;
        App.Services.ChatCleared += OnChatCleared;
        App.Services.ChatMessagePersisted += OnRemoteChatMessage;

        ChatMessages.ItemsSource = _chatBubbles;

        Closed += (_, _) =>
        {
            App.Services.EeLog.RelicRewardScreen -= OnRelicScreenDetected;
            App.Services.ChatCleared -= OnChatCleared;
            App.Services.ChatMessagePersisted -= OnRemoteChatMessage;
        };
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.MakeClickThrough(_hwnd);
        NativeMethods.RegisterHotKey(_hwnd, HotkeyHide, NativeMethods.MOD_NONE, NativeMethods.VK_F9);
        NativeMethods.RegisterHotKey(_hwnd, HotkeyScan, NativeMethods.MOD_NONE, NativeMethods.VK_F10);
        NativeMethods.RegisterHotKey(_hwnd, HotkeyAnalyze, NativeMethods.MOD_NONE, NativeMethods.VK_F11);
        // F7, not Alt: a global bare-Alt hotkey registers unreliably and eats Alt-Tab / in-game Alt binds.
        NativeMethods.RegisterHotKey(_hwnd, HotkeyInteractive, NativeMethods.MOD_NONE, NativeMethods.VK_F7);
        // F12 toggles the chat panel so the user can talk to the advisor from the overlay.
        NativeMethods.RegisterHotKey(_hwnd, HotkeyChat, NativeMethods.MOD_NONE, NativeMethods.VK_F12);
        NativeMethods.RegisterHotKey(_hwnd, HotkeyLock, NativeMethods.MOD_NONE, NativeMethods.VK_F6);
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);

        // Wire drag handlers for all draggable panels (only active when unlocked in interactive mode).
        WireDrag(StatusChip, ChipTransform);
        WireDrag(InteractiveBanner, BannerTransform);
        WireDrag(AnalysisPanel, AnalysisTransform);
        // Chat panel: only the title bar is the drag handle (the rest is interactive content).
        WireChatPanelDrag();
        FollowGameWindow();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY)
        {
            switch (wParam.ToInt32())
            {
                case HotkeyHide:
                    _panelsVisible = !_panelsVisible;
                    Opacity = _panelsVisible ? 1.0 : 0.0;
                    handled = true;
                    break;
                case HotkeyScan:
                    TriggerScan("manual");
                    handled = true;
                    break;
                case HotkeyAnalyze:
                    _ = TriggerAnalyzeScreenAsync();
                    handled = true;
                    break;
                case HotkeyInteractive:
                    ToggleInteractiveMode();
                    handled = true;
                    break;
                case HotkeyChat:
                    ToggleChatPanel();
                    handled = true;
                    break;
                case HotkeyLock:
                    _panelsLocked = !_panelsLocked;
                    UpdateChipText();
                    ShowToast(_panelsLocked ? "🔒 Panels locked" : "🔓 Panels unlocked — drag to reposition");
                    handled = true;
                    break;
            }
        }
        return IntPtr.Zero;
    }

    private void OnRelicScreenDetected()
    {
        if (!App.Services.Settings.OverlayEnabled) return;
        // The reward screen animates in; give it a beat before capturing.
        Dispatcher.Invoke(() =>
        {
            var delay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            delay.Tick += (_, _) => { delay.Stop(); TriggerScan("EE.log"); };
            delay.Start();
        });
    }

    public void TriggerScan(string source)
    {
        ShowToast($"◈ Scanning rewards ({source})…");
        _ = App.Services.Scanner.ScanAsync();
    }

    private void ShowRewards(List<RewardHit> hits)
    {
        Toast.Visibility = Visibility.Collapsed;
        if (hits.Count == 0)
        {
            ShowToast("No reward names recognized — is the reward screen up?");
            return;
        }
        RewardItems.ItemsSource = hits.Select(h => new RewardCard(
            h.MatchedName, h.PlatDisplay, h.DucatDisplay,
            h.IsBest ? Visibility.Visible : Visibility.Collapsed)).ToList();
        RewardPanel.Visibility = Visibility.Visible;
        _hideRewardsTimer.Stop();
        _hideRewardsTimer.Start();
    }

    private void ShowToast(string text)
    {
        ToastText.Text = text;
        Toast.Visibility = Visibility.Visible;
        // Reuse one timer and restart it so a second toast arriving within 4s doesn't get hidden
        // early by the first toast's still-pending Tick.
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void FollowGameWindow()
    {
        var bounds = App.Services.Tracker.ClientBounds;
        if (bounds is not { } rc)
        {
            // No game: park over the primary screen so the user can still test.
            var screen = System.Windows.Forms.Screen.PrimaryScreen?.Bounds
                         ?? new System.Drawing.Rectangle(0, 0, 1280, 720);
            rc = screen;
        }
        var dpi = VisualTreeHelper.GetDpi(this);
        Left = rc.X / dpi.DpiScaleX;
        Top = rc.Y / dpi.DpiScaleY;
        Width = rc.Width / dpi.DpiScaleX;
        Height = rc.Height / dpi.DpiScaleY;
    }

    // ---- interactive mode toggle (F7) ------------------------------------------

    // ---- panel drag (F6 lock/unlock) -------------------------------------------

    private void WireDrag(Border border, TranslateTransform transform)
    {
        border.MouseLeftButtonDown += (_, e) =>
        {
            if (_panelsLocked || !_interactiveMode) return;
            StartDrag(border, transform, e);
        };
        border.MouseMove += (_, e) =>
        {
            if (_dragTarget != border) return;
            ContinueDrag(e);
        };
        border.MouseLeftButtonUp += (_, e) =>
        {
            if (_dragTarget != border) return;
            EndDrag();
        };
    }

    private void WireChatPanelDrag()
    {
        // Find the title bar border (first child of DockPanel inside ChatPanel) and wire drag there.
        if (ChatPanel.Child is DockPanel dp)
        {
            foreach (var child in dp.Children)
            {
                if (child is Border titleBar && DockPanel.GetDock(titleBar) == Dock.Top)
                {
                    titleBar.MouseLeftButtonDown += (_, e) =>
                    {
                        if (_panelsLocked || !_interactiveMode) return;
                        StartDrag(ChatPanel, ChatTransform, e);
                    };
                    break;
                }
            }
        }
        // Move/up are on the ChatPanel itself so the drag continues even if the cursor
        // leaves the narrow title bar during a fast swipe.
        ChatPanel.MouseMove += (_, e) =>
        {
            if (_dragTarget != ChatPanel) return;
            ContinueDrag(e);
        };
        ChatPanel.MouseLeftButtonUp += (_, e) =>
        {
            if (_dragTarget != ChatPanel) return;
            EndDrag();
        };
    }

    private void StartDrag(UIElement element, TranslateTransform transform, MouseButtonEventArgs e)
    {
        var pos = e.GetPosition(this);
        _dragTarget = element;
        _dragTransform = transform;
        _dragStart = pos;
        _dragOrigin = new Point(transform.X, transform.Y);
        element.CaptureMouse();
        e.Handled = true;
    }

    private void ContinueDrag(MouseEventArgs e)
    {
        if (_dragTransform is null) return;
        var pos = e.GetPosition(this);
        _dragTransform.X = _dragOrigin.X + (pos.X - _dragStart.X);
        _dragTransform.Y = _dragOrigin.Y + (pos.Y - _dragStart.Y);
        e.Handled = true;
    }

    private void EndDrag()
    {
        _dragTarget?.ReleaseMouseCapture();
        _dragTarget = null;
        _dragTransform = null;
    }

    private void UpdateChipText()
    {
        ChipText.Text = _panelsLocked
            ? "F6 lock/unlock · F7 HUD · F8 app · F9 hide · F10 scan · F11 analyze"
            : "F6 🔒 LOCK · F7 HUD · F8 app · F9 hide · F10 scan · F11 analyze";
    }

    private void ToggleInteractiveMode()
    {
        _interactiveMode = !_interactiveMode;
        if (_interactiveMode)
        {
            NativeMethods.MakeInteractive(_hwnd);
            InteractiveBanner.Visibility = Visibility.Visible;
            // Lock panels by default when entering interactive mode.
            _panelsLocked = true;
            UpdateChipText();
        }
        else
        {
            NativeMethods.MakeClickThrough(_hwnd);
            InteractiveBanner.Visibility = Visibility.Collapsed;
            // Leaving interactive mode also folds the chat — the user has to re-engage to use it.
            if (ChatPanel.Visibility == Visibility.Visible) SetChatOpen(false);
        }
    }

    // ---- AI advisor chat panel (F12 / 💬 Chat button) --------------------------

    /// <summary>Show or hide the chat panel. Folding it cancels any in-flight request.</summary>
    private void ToggleChatPanel() => SetChatOpen(ChatPanel.Visibility != Visibility.Visible);

    private void SetChatOpen(bool open)
    {
        if (open)
        {
            // F7 is required to type: if click-through is on, the user can't reach the textbox.
            if (!_interactiveMode)
            {
                ShowToast("Press F7 first to make the HUD interactive, then F12 for chat.");
                ToggleInteractiveMode();
            }
            // The chat and the F11 analysis panel both anchor bottom-right; never show both
            // at the same time or they overlap and race on the dispatcher. The F11 result
            // is always streamed into the chat when the chat is open.
            if (AnalysisPanel.Visibility == Visibility.Visible)
            {
                _analysisCts?.Cancel();
                AnalysisPanel.Visibility = Visibility.Collapsed;
            }
            EnsureChatHistoryLoaded();
            ChatPanel.Visibility = Visibility.Visible;
            ChatInputBox.Focus();
        }
        else
        {
            ChatPanel.Visibility = Visibility.Collapsed;
            _chatCts?.Cancel();
        }
    }

    private void OnRemoteChatMessage(string role, string content)
    {
        // Ignore messages we ourselves just persisted (the event is global).
        if (_chatBubbles.Count > 0 && _chatBubbles[^1].Role == role && _chatBubbles[^1].Content == content) return;
        Dispatcher.Invoke(() =>
        {
            _chatBubbles.Add(new ChatBubble(role, content));
            ScrollChatToEnd();
        });
    }

    private void OnChatCleared()
    {
        Dispatcher.Invoke(() =>
        {
            _chatCts?.Cancel();
            _chatBubbles.Clear();
            _chatHistoryLoaded = false;
        });
    }

    private void OnChatToggle(object sender, RoutedEventArgs e) => ToggleChatPanel();
    private void OnChatClose(object sender, RoutedEventArgs e) => SetChatOpen(false);

    private void OnChatInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            _ = SendChatAsync();
        }
    }

    private void OnChatSend(object sender, RoutedEventArgs e) => _ = SendChatAsync();

    private void EnsureChatHistoryLoaded()
    {
        if (_chatHistoryLoaded) return;
        _chatHistoryLoaded = true;
        try
        {
            // Reuse the same persistence the main AdvisorView uses, so the overlay chat and
            // the advisor tab stay in sync.
            foreach (var row in App.Services.LoadRecentChatHistory(40))
                _chatBubbles.Add(ChatBubble.FromRow(row.Role, row.Content));
            ScrollChatToEnd();
        }
        catch (Exception ex)
        {
            ShowToast($"Chat history load failed: {ex.Message}");
        }
    }

    private async Task SendChatAsync()
    {
        if (_chatBusy) return;
        var s = App.Services;

        if (!s.Settings.HasApiKey)
        {
            ShowToast("⚠ No API key — add one in Settings to use the chat.");
            return;
        }

        var text = ChatInputBox.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return;

        EnsureChatHistoryLoaded();

        // If the latest persisted message has the same text, avoid re-sending the duplicate.
        if (_chatBubbles.LastOrDefault() is { Role: "user" } dup && dup.Content == text) return;

        _chatBusy = true;
        ChatInputBox.Clear();
        ChatSendBtn.IsEnabled = false;

        var userBubble = new ChatBubble("user", text);
        _chatBubbles.Add(userBubble);
        ScrollChatToEnd();
        PersistChatMessage("user", text);

        // Build the message list the AI sees: a system context plus recent history.
        _chatCts?.Cancel();
        _chatCts = new CancellationTokenSource();
        var ct = _chatCts.Token;

        var history = _chatBubbles
            .TakeLast(24)
            .Select(b => new ChatMsg { Role = b.Role, Content = b.Content })
            .ToList();
        // System message goes first; the user's last bubble stays in place.
        var retrievalQuery = history.LastOrDefault(m => m.Role == "user")?.Content;
        await s.Intelligence.PrimeAdvisorQueryAsync(retrievalQuery, ct);
        var messages = new List<ChatMsg> { s.Context.BuildSystemMessage(retrievalQuery) };
        messages.AddRange(history);

        var assistantBubble = new ChatBubble("assistant", "…");
        _chatBubbles.Add(assistantBubble);
        ScrollChatToEnd();

        var full = new StringBuilder();
        try
        {
            await foreach (var delta in s.OpenRouter.StreamChatAsync(messages, s.Settings.ChatModel, ct))
            {
                full.Append(delta);
                assistantBubble.Content = full.ToString();
            }
            assistantBubble.Content = full.Length == 0 ? "(no response)" : full.ToString();
            PersistChatMessage("assistant", assistantBubble.Content);

            // Check for embedded cuda-action blocks the AI may have proposed.
            var actions = App.Services.AiCommands.ParseActions(full.ToString());
            if (actions.Count > 0)
                _ = Dispatcher.BeginInvoke(() => ShowActionDialog(actions));
        }
        catch (OperationCanceledException)
        {
            // A newer send (or panel close) cancelled us — keep whatever we streamed.
            assistantBubble.Content = full.Length == 0 ? "[cancelled]" : full.ToString() + " [cancelled]";
        }
        catch (Exception ex)
        {
            assistantBubble.Content = $"Error: {ex.Message}";
        }
        finally
        {
            _chatBusy = false;
            ChatSendBtn.IsEnabled = true;
            ScrollChatToEnd();
        }
    }

    private void PersistChatMessage(string role, string content)
    {
        try { App.Services.PersistChatMessage(role, content); }
        catch { /* persistence failure shouldn't break the chat */ }
    }

    private void ScrollChatToEnd()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            ChatScroll.UpdateLayout();
            ChatScroll.ScrollToEnd();
        }));
    }

    // ---- AI screen analysis (F11) -------------------------------------------------

    private CancellationTokenSource? _analysisCts;

    private async Task TriggerAnalyzeScreenAsync()
    {
        var s = App.Services;

        if (!s.Settings.HasApiKey)
        {
            ShowToast("⚠ No API key — add one in Settings to use screen analysis.");
            return;
        }

        var bounds = s.Tracker.CaptureBounds;
        var frame = s.Capture.CaptureRegion(bounds);
        if (frame is null)
        {
            ShowToast("⚠ Capture failed — is the game in Borderless/Windowed?");
            return;
        }

        var b64 = ScreenCaptureService.ToBase64Png(frame);
        s.Context.LastScreenNote = $"Screenshot analyzed via overlay at {DateTimeOffset.Now:HH:mm:ss} " +
                                   $"({(s.Tracker.GameRunning ? "game window" : "primary screen")}, {s.Capture.LastBackend})";

        // Push the image preview to the Advisor tab so the user can see what was captured.
        s.RaiseScreenCaptured(b64, $"F11 overlay → {s.Settings.VisionModel}");

        // If the chat panel is open, the F11 result streams into the chat and the
        // bottom-right analysis panel stays hidden — they share the same anchor and
        // overlapping them races on the dispatcher. Capture the routing decision now
        // so the streaming loop below can branch consistently.
        bool streamIntoChat = ChatPanel.Visibility == Visibility.Visible;
        ChatBubble? placeholder = null;
        if (streamIntoChat)
        {
            // Make sure the chat has its history loaded so the analysis lands in-bubble.
            EnsureChatHistoryLoaded();
            // Add a placeholder assistant bubble that we'll fill in as the stream returns.
            placeholder = new ChatBubble("assistant", "Analyzing…");
            _chatBubbles.Add(placeholder);
            ScrollChatToEnd();
            // Persist the user turn immediately so the chat history and Advisor tab stay in sync.
            // DB-only (no broadcast): the bubble above already reflects it in this window.
            s.InsertChatMessage("user", "[F11 overlay screen analysis]");
        }
        else
        {
            ShowToast($"◈ Analyzing screen ({s.Capture.LastBackend}) → {s.Settings.VisionModel}…");
            AnalysisText.Text = "Analyzing…";
            AnalysisPanel.Visibility = Visibility.Visible;
        }

        _analysisCts?.Cancel();
        _analysisCts = new CancellationTokenSource();
        var ct = _analysisCts.Token;

        var messages = new List<ChatMsg>
        {
            s.Context.BuildSystemMessage("Analyze the visible Warframe screen loadout items mods missions rewards and progression"),
            new ChatMsg
            {
                Role = "user",
                Content = "Here is a screenshot of my current Warframe screen. Read everything visible — " +
                          "loadout, inventory, mission results, mod config, standing, whatever is on screen — " +
                          "and give concise, actionable advice. Be specific about what you see.",
                ImageBase64Png = b64,
            },
        };

        var full = new StringBuilder();
        try
        {
            await foreach (var delta in s.OpenRouter.StreamChatAsync(messages, s.Settings.VisionModel, ct))
            {
                full.Append(delta);
                if (streamIntoChat)
                {
                    placeholder!.Content = full.ToString();
                }
                else
                {
                    // Throttle UI updates: only refresh every ~80 chars or so
                    if (full.Length % 80 < delta.Length)
                        AnalysisText.Text = full.ToString();
                }
            }
            var result = full.ToString();

            if (streamIntoChat)
            {
                placeholder!.Content = result.Length == 0 ? "(no response)" : result;
                s.InsertChatMessage("assistant", result);
                ScrollChatToEnd();
            }
            else
            {
                AnalysisText.Text = result;
                // Persist to Advisor chat history so it shows up there too (and in the overlay chat)
                // next time either surface loads history — not broadcast live since neither chat panel is open.
                s.InsertChatMessage("user", "[F11 overlay screen analysis]");
                s.InsertChatMessage("assistant", result);
            }
        }
        catch (OperationCanceledException)
        {
            // User pressed F11 again, or the chat panel took over. Don't surface an error.
            if (streamIntoChat)
            {
                placeholder!.Content = full.Length == 0 ? "[cancelled]" : full.ToString() + " [cancelled]";
            }
        }
        catch (Exception ex)
        {
            if (streamIntoChat)
            {
                placeholder!.Content = $"Error: {ex.Message}";
            }
            else
            {
                AnalysisText.Text = $"Error: {ex.Message}";
            }
        }
    }

    private void OnAnalysisClose(object sender, RoutedEventArgs e)
    {
        _analysisCts?.Cancel();
        AnalysisPanel.Visibility = Visibility.Collapsed;
    }

    // ---- cuda-action: AI proposed database changes ----------------------------

    /// <summary>Show the action confirmation dialog; if approved, execute and toast results.</summary>
    private void ShowActionDialog(List<AiAction> actions)
    {
        var dialog = new AiActionDialog(actions, App.Services.AiCommands) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Results.Count == 0) return;

        var ok = dialog.Results.Count(r => r.Success);
        var total = dialog.Results.Count;
        ShowToast($"✓ Applied {ok}/{total} AI changes");
        var lines = dialog.Results.Select(r => r.Success ? $"✓ {r.Message}" : $"✕ {r.Message}");
        var summary = $"Applied {ok}/{total} changes:\n" + string.Join("\n", lines);
        PersistChatMessage("system", summary);
    }

    protected override void OnClosed(EventArgs e)
    {
        _followTimer.Stop();
        _hideRewardsTimer.Stop();
        _toastTimer.Stop();
        _chatCts?.Cancel();
        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.UnregisterHotKey(_hwnd, HotkeyHide);
            NativeMethods.UnregisterHotKey(_hwnd, HotkeyScan);
            NativeMethods.UnregisterHotKey(_hwnd, HotkeyAnalyze);
            NativeMethods.UnregisterHotKey(_hwnd, HotkeyInteractive);
            NativeMethods.UnregisterHotKey(_hwnd, HotkeyChat);
            NativeMethods.UnregisterHotKey(_hwnd, HotkeyLock);
        }
        base.OnClosed(e);
    }
}

/// <summary>One rendered message in the overlay chat panel. Inherits INotifyPropertyChanged so the
/// last assistant bubble can update in-place while the AI stream fills it in.</summary>
public sealed class ChatBubble : INotifyPropertyChanged
{
    private string _content;

    public ChatBubble(string role, string content)
    {
        Role = role;
        _content = content;
    }

    public string Role { get; }
    public string RoleLabel => Role == "user" ? "▶ YOU" : "✦ CUDA";
    public Brush BubbleBrush => Role == "user" ? AppBrush("Brush.PanelAlt") : AppBrush("Brush.Panel");
    public Brush RoleAccent => AppBrush("Brush.Accent") ?? Brushes.LimeGreen;

    private static Brush AppBrush(string key) =>
        Application.Current?.Resources[key] as Brush ?? Brushes.Transparent;

    public string Content
    {
        get => _content;
        set { if (_content != value) { _content = value; OnChanged(); OnChanged(nameof(DisplayContent)); } }
    }

    /// <summary>Content with basic markdown stripped for plain-text display in the overlay TextBlock.</summary>
    public string DisplayContent => StripMarkdown(_content);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>Strip common markdown formatting so the overlay TextBlock shows readable text.</summary>
    private static string StripMarkdown(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        // Fenced code blocks: ```lang ... ``` → just the content
        text = Regex.Replace(text, @"```\w*\r?\n?", "");
        // Inline code: `text` → text
        text = Regex.Replace(text, @"`([^`]+)`", "$1");
        // Bold: **text** or __text__ → text
        text = Regex.Replace(text, @"\*\*(.+?)\*\*", "$1");
        text = Regex.Replace(text, @"__(.+?)__", "$1");
        // Italic: *text* or _text_ → text (but not inside words like some_item)
        text = Regex.Replace(text, @"(?<!\w)\*(.+?)\*(?!\w)", "$1");
        text = Regex.Replace(text, @"(?<!\w)_(.+?)_(?!\w)", "$1");
        // Headings: ### text → text
        text = Regex.Replace(text, @"^#{1,6}\s+", "", RegexOptions.Multiline);
        // Bullet points: - text → • text
        text = Regex.Replace(text, @"^- ", "• ", RegexOptions.Multiline);
        // Numbered lists: 1. text → 1) text
        text = Regex.Replace(text, @"^(\d+)\.\s+", "$1) ", RegexOptions.Multiline);
        // Links: [text](url) → text
        text = Regex.Replace(text, @"\[([^\]]+)\]\([^)]+\)", "$1");
        // Horizontal rules: --- or *** → line
        text = Regex.Replace(text, @"^[-*]{3,}$", "─────────", RegexOptions.Multiline);
        return text.TrimEnd();
    }

    /// <summary>Build a bubble from a DB row, stripping any system-level metadata tags.</summary>
    public static ChatBubble FromRow(string role, string content)
    {
        var clean = role == "user" && content.StartsWith("[F11 overlay", StringComparison.Ordinal)
            ? "(F11 screen analysis attached)"
            : content;
        return new ChatBubble(role, clean);
    }
}
