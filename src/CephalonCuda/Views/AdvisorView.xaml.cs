using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using CephalonCuda.Models;
using CephalonCuda.Services;
using Microsoft.Web.WebView2.Core;

namespace CephalonCuda.Views;

public partial class AdvisorView : UserControl
{
    private readonly List<ChatMsg> _history = [];
    private bool _initStarted;
    private bool _pageReady;
    private CancellationTokenSource? _chatCts;
    private string? _pendingPrompt;

    public AdvisorView()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            ModelLabel.Text = $"chat: {App.Services.Settings.ChatModel} · vision: {App.Services.Settings.VisionModel}";
            // Subscribe to overlay screen captures so they appear as image previews in this chat.
            App.Services.ScreenCaptured += OnOverlayScreenCaptured;
            App.Services.ChatMessagePersisted += OnRemoteChatMessage;
            App.Services.AdvisorPromptRequested += OnAdvisorPromptRequested;
            ThemeService.ThemeChanged += OnThemeChanged;
            if (_initStarted) return;
            _initStarted = true;
            await InitWebViewAsync();
        };
        Unloaded += (_, _) =>
        {
            App.Services.ScreenCaptured -= OnOverlayScreenCaptured;
            App.Services.ChatMessagePersisted -= OnRemoteChatMessage;
            App.Services.AdvisorPromptRequested -= OnAdvisorPromptRequested;
            ThemeService.ThemeChanged -= OnThemeChanged;
        };
    }


    private void OnAdvisorPromptRequested(string prompt) => Dispatcher.Invoke(() =>
    {
        _pendingPrompt = prompt;
        if (_pageReady) PostJson(new { type = "prefill", text = prompt });
    });

    private void OnThemeChanged() => Dispatcher.Invoke(() =>
    {
        ApplyWebViewBackground();
        PushTheme();
    });

    private void ApplyWebViewBackground()
    {
        var c = ThemeService.CurrentBackground;
        Web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(c.A, c.R, c.G, c.B);
    }

    private void PushTheme() => PostJson(new { type = "theme", vars = ThemeService.WebVars() });

    private void OnOverlayScreenCaptured(string base64Png, string label) =>
        Dispatcher.Invoke(() => ShowImagePreview(base64Png, label));

    private async Task InitWebViewAsync()
    {
        try
        {
            var dataDir = Path.Combine(Db.AppDataDir, "webview2");
            var env = await CoreWebView2Environment.CreateAsync(null, dataDir);
            await Web.EnsureCoreWebView2Async(env);

            ApplyWebViewBackground();
            var wv = Web.CoreWebView2;
            wv.Settings.AreDefaultContextMenusEnabled = false;
            wv.Settings.IsStatusBarEnabled = false;
            wv.Settings.AreDevToolsEnabled = false;
            wv.WebMessageReceived += OnWebMessage;

            wv.Navigate(new Uri(ExtractAdvisorHtml()).AbsoluteUri);
            LoadHistoryFromDb();
        }
        catch (Exception ex)
        {
            Web.Visibility = Visibility.Collapsed;
            FallbackText.Visibility = Visibility.Visible;
            FallbackText.Text =
                "The AI Advisor needs the WebView2 Runtime (preinstalled on Windows 11; " +
                "otherwise download 'WebView2 Evergreen Runtime' from Microsoft).\n\nError: " + ex.Message;
        }
    }

    /// <summary>The chat UI ships embedded in the exe (single-file publish); extract fresh on every start.</summary>
    private static string ExtractAdvisorHtml()
    {
        var path = Path.Combine(Db.AppDataDir, "advisor.html");
        using var stream = typeof(AdvisorView).Assembly.GetManifestResourceStream("CephalonCuda.Assets.advisor.html")
            ?? throw new InvalidOperationException("Embedded resource CephalonCuda.Assets.advisor.html missing.");
        using var file = File.Create(path);
        stream.CopyTo(file);
        return path;
    }

    private void LoadHistoryFromDb()
    {
        _history.Clear();
        _history.AddRange(App.Services.LoadRecentChatHistory(40)
            .Select(r => new ChatMsg { Role = r.Role, Content = r.Content }));
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.TryGetWebMessageAsString());
            var type = doc.RootElement.GetProperty("type").GetString();
            switch (type)
            {
                case "ready":
                    _pageReady = true;
                    PushTheme();
                    if (_history.Count > 0)
                        PostJson(new { type = "history", messages = _history.Select(m => new { role = m.Role, content = m.Content }) });
                    if (!string.IsNullOrWhiteSpace(_pendingPrompt))
                        PostJson(new { type = "prefill", text = _pendingPrompt });
                    break;
                case "user":
                    var text = doc.RootElement.GetProperty("text").GetString() ?? "";
                    if (text.Length > 0)
                        _ = RunChatAsync(new ChatMsg { Role = "user", Content = text }, App.Services.Settings.ChatModel);
                    break;
            }
        }
        catch { /* malformed message from page */ }
    }

    private async Task RunChatAsync(ChatMsg userMsg, string model)
    {
        var s = App.Services;
        if (!s.Settings.HasApiKey)
        {
            PostJson(new { type = "error", text = "No OpenRouter API key configured. Add one under **Settings**." });
            return;
        }

        _chatCts?.Cancel();
        _chatCts = new CancellationTokenSource();
        var ct = _chatCts.Token;

        _history.Add(userMsg);
        Persist(userMsg.Role, userMsg.Content + (userMsg.ImageBase64Png is null ? "" : " [screenshot attached]"));

        // Prime per-item live market orders when the question names an item, then rebuild context.
        await s.Intelligence.PrimeAdvisorQueryAsync(userMsg.Content, ct);
        var messages = new List<ChatMsg> { s.Context.BuildSystemMessage(userMsg.Content) };
        messages.AddRange(_history.TakeLast(24).Select(m => new ChatMsg
        {
            Role = m.Role,
            Content = m.Content,
            ImageBase64Png = ReferenceEquals(m, userMsg) ? m.ImageBase64Png : null, // only the newest image is resent
        }));

        PostJson(new { type = "start" });
        var full = "";
        try
        {
            await foreach (var delta in s.OpenRouter.StreamChatAsync(messages, model, ct))
            {
                full += delta;
                PostJson(new { type = "delta", text = delta });
            }
            PostJson(new { type = "done" });
            _history.Add(new ChatMsg { Role = "assistant", Content = full });
            Persist("assistant", full);

            // Check for embedded cuda-action blocks the AI may have proposed.
            var actions = App.Services.AiCommands.ParseActions(full);
            if (actions.Count > 0)
                _ = Dispatcher.BeginInvoke(() => ShowActionDialog(actions));
        }
        catch (OperationCanceledException)
        {
            PostJson(new { type = "done" });
        }
        catch (Exception ex)
        {
            PostJson(new { type = "error", text = ex.Message });
        }
    }

    private void Persist(string role, string content) => App.Services.PersistChatMessage(role, content);

    private void OnRemoteChatMessage(string role, string content)
    {
        // Ignore messages we ourselves just persisted (the event is global).
        if (_history.Count > 0 && _history[^1].Role == role && _history[^1].Content == content) return;
        Dispatcher.Invoke(() =>
        {
            _history.Add(new ChatMsg { Role = role, Content = content });
            PostJson(new { type = "remoteMessage", role, content });
        });
    }

    private void PostJson(object payload)
    {
        if (!_pageReady) return;
        Dispatcher.Invoke(() =>
        {
            try { Web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(payload)); }
            catch { }
        });
    }

    /// <summary>Re-encode a base64 PNG to a smaller thumbnail (keeps WebView payloads manageable).</summary>
    private static string MakeThumbnailBase64(string base64Png, int maxWidth)
    {
        try
        {
            var bytes = Convert.FromBase64String(base64Png);
            using var ms = new MemoryStream(bytes);
            var bitmap = new System.Windows.Media.Imaging.BitmapImage();
            bitmap.BeginInit();
            bitmap.StreamSource = ms;
            bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            if (maxWidth > 0) bitmap.DecodePixelWidth = maxWidth;
            bitmap.EndInit();
            bitmap.Freeze();

            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var outMs = new MemoryStream();
            encoder.Save(outMs);
            return Convert.ToBase64String(outMs.ToArray());
        }
        catch
        {
            return base64Png; // fallback: send original if resize fails
        }
    }

    // ---- vision fallback: DXGI snap -> multimodal model ----------------------

    /// <summary>
    /// Show a thumbnail preview of a captured screenshot in the chat. Used for overlay screen
    /// captures, which arrive as an already-PNG-encoded base64 string (cross-window event, no
    /// access to the original pixel buffer) and so must be decoded and re-shrunk here.
    /// </summary>
    internal void ShowImagePreview(string base64Png, string label) =>
        PostImagePreview(MakeThumbnailBase64(base64Png, 800), label);

    private void PostImagePreview(string thumbBase64Png, string label) =>
        PostJson(new { type = "userImage", thumb = thumbBase64Png, label });

    private async void OnSnapScreen(object sender, RoutedEventArgs e)
    {
        var s = App.Services;
        var mainWin = Application.Current.MainWindow;

        // If the app overlaps the game, hide it for a beat so the capture shows the game, not us.
        // Hide/Show (not Minimize) is instant, skips the taskbar animation, and preserves
        // WindowState — restoring to Normal used to wipe a maximized window.
        bool hidden = false;
        if (s.Tracker.GameRunning && mainWin is not null && mainWin.IsVisible)
        {
            mainWin.Hide();
            await Task.Delay(250); // give the compositor a frame to expose the game
            hidden = true;
        }

        var bounds = s.Tracker.CaptureBounds;
        var frame = s.Capture.CaptureRegion(bounds);

        if (hidden && mainWin is not null)
        {
            mainWin.Show();
            mainWin.Activate();
        }

        if (frame is null)
        {
            PostJson(new { type = "error", text = "Screen capture failed. If the game is in exclusive fullscreen, switch to Borderless." });
            return;
        }

        var b64 = ScreenCaptureService.ToBase64Png(frame);

        // Show the screenshot preview in the chat so the user can see what was sent. The thumbnail
        // is encoded directly from the raw frame (already in hand here) instead of round-tripping
        // through the full-size b64 above.
        PostImagePreview(ScreenCaptureService.ToBase64Png(frame, maxWidth: 800), $"Captured via {s.Capture.LastBackend} → {s.Settings.VisionModel}");

        s.Context.LastScreenNote = $"Screenshot sent to vision model at {DateTimeOffset.Now:HH:mm:ss} " +
                                   $"({(s.Tracker.GameRunning ? "game window" : "primary screen")}, {s.Capture.LastBackend})";

        _ = RunChatAsync(new ChatMsg
        {
            Role = "user",
            Content = "Here is a screenshot of my current screen (likely my Warframe loadout, inventory or mission " +
                      "results). Read everything relevant, summarize what you see, and give your best advice on it.",
            ImageBase64Png = b64,
        }, s.Settings.VisionModel);
    }

    /// <summary>OCR the current screen into the advisor's rolling memory (no vision-model call spent).</summary>
    private async void OnReadScreen(object sender, RoutedEventArgs e)
    {
        var s = App.Services;
        var frame = s.Capture.CaptureRegion(s.Tracker.CaptureBounds);
        if (frame is null)
        {
            PostJson(new { type = "error", text = "Screen capture failed. If the game is in exclusive fullscreen, switch to Borderless." });
            return;
        }
        try
        {
            var text = await s.Ocr.RecognizeTextAsync(frame);
            if (string.IsNullOrWhiteSpace(text))
            {
                PostJson(new { type = "userNote", text = "🔍 OCR found no readable text on that screen." });
                return;
            }
            var label = s.Tracker.CurrentNodeOrScreen();
            s.Context.AddObservation(label, text);
            var snippet = text.Replace('\n', ' ');
            if (snippet.Length > 90) snippet = snippet[..90] + "…";
            PostJson(new { type = "userNote", text = $"🔍 Read screen → memory ({s.Context.ObservationCount} kept): \"{snippet}\"" });
        }
        catch (Exception ex)
        {
            PostJson(new { type = "error", text = $"OCR failed: {ex.Message}" });
        }
    }

    private void OnForgetCaptures(object sender, RoutedEventArgs e)
    {
        App.Services.Context.ClearObservations();
        App.Services.Context.LastScreenNote = null;
        PostJson(new { type = "userNote", text = "🧠 Cleared screen captures from advisor memory." });
    }

    private void OnClearChat(object sender, RoutedEventArgs e)
    {
        _chatCts?.Cancel();
        App.Services.Db.Exec("DELETE FROM chat_messages");
        _history.Clear();
        PostJson(new { type = "clear" });
        App.Services.RaiseChatCleared();
    }

    // ---- cuda-action: AI proposed database changes ----------------------------

    /// <summary>Show the action confirmation dialog; if approved, execute and post results.</summary>
    private void ShowActionDialog(List<AiAction> actions)
    {
        var dialog = new AiActionDialog(actions, App.Services.AiCommands)
        {
            Owner = Window.GetWindow(this),
        };
        if (dialog.ShowDialog() != true || dialog.Results.Count == 0) return;

        var ok = dialog.Results.Count(r => r.Success);
        var total = dialog.Results.Count;
        var lines = dialog.Results.Select(r => r.Success ? $"✓ {r.Message}" : $"✕ {r.Message}");
        var summary = $"Applied {ok}/{total} changes:\n" + string.Join("\n", lines);
        // Persist broadcasts via ChatMessagePersisted → OnRemoteChatMessage, so no separate
        // PostJson(userNote) needed — that would duplicate the message in the chat.
        Persist("system", summary);
    }
}
