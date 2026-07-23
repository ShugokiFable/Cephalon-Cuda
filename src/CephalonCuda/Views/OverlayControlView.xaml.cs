using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CephalonCuda.Models;

namespace CephalonCuda.Views;

public partial class OverlayControlView : UserControl
{
    private readonly DispatcherTimer _refresh;
    private Action<List<RewardHit>>? _scanHandler;
    private Action<EeLogEvent>? _logHandler;

    public OverlayControlView()
    {
        InitializeComponent();
        AutoScanBox.IsChecked = App.Services.Settings.OverlayEnabled;

        _refresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _refresh.Tick += (_, _) => RefreshStatus();

        Loaded += (_, _) =>
        {
            RefreshStatus();
            RefreshLogEvents();
            _refresh.Start();
            if (_scanHandler is null)
            {
                _scanHandler = hits => Dispatcher.Invoke(() => ShowScan(hits));
                App.Services.Scanner.ScanCompleted += _scanHandler;
            }
            if (_logHandler is null)
            {
                _logHandler = _ => Dispatcher.Invoke(RefreshLogEvents);
                App.Services.EeLog.EventParsed += _logHandler;
            }
        };
        Unloaded += (_, _) =>
        {
            _refresh.Stop();
            if (_scanHandler is not null) { App.Services.Scanner.ScanCompleted -= _scanHandler; _scanHandler = null; }
            if (_logHandler is not null) { App.Services.EeLog.EventParsed -= _logHandler; _logHandler = null; }
        };
    }

    private void RefreshStatus()
    {
        var s = App.Services;
        EngineStatus.Text =
            $"Game window   : {(s.Tracker.GameRunning ? $"detected {s.Tracker.ClientBounds?.Width}x{s.Tracker.ClientBounds?.Height}{(s.Tracker.GameFocused ? " (focused)" : "")}" : "not found")}\n" +
            $"Capture       : {(s.Capture.LastBackend == "none" ? "not used yet" : s.Capture.LastBackend)}\n" +
            $"Windows OCR   : available\n" +
            $"Tesseract     : {(s.Ocr.TesseractAvailable ? "loaded (tesseract53.dll)" : "not installed (optional)")}\n" +
            $"EE.log        : {(s.EeLog.FileExists ? s.Settings.EeLogPath : "NOT FOUND — check Settings")}";

        var main = Application.Current.MainWindow as MainWindow;
        ToggleButton.Content = main?.OverlayVisible == true ? "▣ Hide overlay" : "▣ Launch overlay";
    }

    private void RefreshLogEvents() =>
        LogEvents.ItemsSource = App.Services.EeLog.RecentEvents(25)
            .AsEnumerable().Reverse()
            .Select(ev => $"{ev.TimeDisplay}  [{ev.Kind}] {ev.Detail}")
            .ToList();

    private void ShowScan(List<RewardHit> hits)
    {
        ScanResults.ItemsSource = hits.Count == 0
            ? new List<string> { "(nothing recognized)" }
            : hits.Select(h =>
                $"{(h.IsBest ? "★ " : "  ")}{h.MatchedName} — {h.PlatDisplay} {h.DucatDisplay} (match {h.Score:P0}, raw '{h.RawText}')").ToList();
        ScanStatus.Text = $"Scanned {DateTime.Now:HH:mm:ss} via {App.Services.Capture.LastBackend}.";
    }

    private void OnAutoScanChanged(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) App.Services.Settings.OverlayEnabled = AutoScanBox.IsChecked == true;
    }

    private void OnToggleOverlay(object sender, RoutedEventArgs e)
    {
        if (Application.Current.MainWindow is MainWindow main)
        {
            if (main.OverlayVisible) main.EnsureOverlay().Hide();
            else main.EnsureOverlay().Show();
            main.SyncOverlayButton();
            RefreshStatus();
        }
    }

    private async void OnTestScan(object sender, RoutedEventArgs e)
    {
        ScanStatus.Text = "Scanning…";
        await App.Services.Scanner.ScanAsync();
    }
}
