using System.IO;
using System.Windows;
using System.Windows.Threading;
using CephalonCuda.Services;
using CephalonCuda.Views;

namespace CephalonCuda;

public partial class App : Application
{
    public static AppServices Services { get; private set; } = null!;
    private TrayService? _tray;
    private bool _smokeMode;

    public App()
    {
        InitializeComponent();
        // Register the initial runtime theme tokens before loading the shared style system.
        // Every visual token is later replaced through DynamicResource, so themes update
        // the complete live visual tree without relying on mutable/frozen WPF Freezables.
        ThemeService.RegisterBrushes(Resources);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _smokeMode = e.Args.Contains("--smoke");

        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogCrash(args.ExceptionObject as Exception, "domain");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogCrash(args.Exception, "task");
            args.SetObserved();
        };

        Services = new AppServices();

        // Merge the shared control/style system after runtime tokens exist.
        // Despite the historical filename, Verv.xaml now contains palette-neutral styles.
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("Themes/Verv.xaml", UriKind.Relative)
        });

        // `--verify-data`: headless end-to-end check of every live API integration.
        // Writes a report to %LocalAppData%\CephalonCuda\logs\verify.log; exit code = failure count.
        if (e.Args.Contains("--verify-data"))
        {
            _ = Task.Run(RunDataVerificationAsync);
            return;
        }

        Services.Start();

        // Retint the shared theme brushes before any window renders them.
        ThemeService.ApplyFromSettings(Services.Settings);

        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        _tray = new TrayService(window);
        if (Services.Settings.OverlayOnStartup)
        {
            window.EnsureOverlay().Show();
            window.SyncOverlayButton();
        }

        // `--smoke`: automated startup verification — instantiate every view + the overlay, then exit 0.
        if (_smokeMode)
        {
            _ = window.Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await Task.Delay(1200);
                    await window.SmokeCycleAsync();
                    Shutdown(0);
                }
                catch (Exception ex)
                {
                    LogCrash(ex, "smoke");
                    Shutdown(2);
                }
            });
        }
    }

    private static async Task RunDataVerificationAsync()
    {
        var report = new System.Text.StringBuilder();
        int failures = 0;

        async Task Check(string name, Func<Task<string>> probe)
        {
            // Materialize the result before touching the builder — StringBuilder's interpolation
            // handler appends incrementally, which leaves partial lines when the probe throws.
            try
            {
                var result = await probe();
                report.AppendLine($"[OK]   {name}: {result}");
            }
            catch (Exception ex) { failures++; report.AppendLine($"[FAIL] {name}: {ex.Message}"); }
        }

        async Task CheckOptional(string name, Func<Task<string>> probe)
        {
            try
            {
                var result = await probe();
                report.AppendLine($"[OK]   {name}: {result}");
            }
            catch (Exception ex) { report.AppendLine($"[WARN] {name}: optional legacy feed unavailable: {ex.Message}"); }
        }

        var s = Services;
        await Check("database schema / search", () =>
        {
            var schema = s.Db.Scalar<long>("PRAGMA user_version");
            if (schema != Db.SchemaVersion) throw new InvalidOperationException($"schema {schema}, expected {Db.SchemaVersion}");
            return Task.FromResult($"schema v{schema}, FTS {(s.Db.FtsAvailable ? "enabled" : "fallback")}");
        });
        await Check("Tenno Path / shop guard", () =>
        {
            var progress = s.Onboarding.Progress();
            if (progress.Total < 15) throw new InvalidOperationException($"only {progress.Total} roadmap tasks seeded");
            var credits = s.Onboarding.EvaluatePurchase("150,000 Credits");
            if (credits.Verdict != "NEVER") throw new InvalidOperationException($"Credits verdict was {credits.Verdict}");
            var returning = s.Returns.GetBriefing();
            if (returning.DoNow.Count == 0) throw new InvalidOperationException("return briefing produced no next actions");
            return Task.FromResult($"{progress.Total} core roadmap tasks; Credits verdict={credits.Verdict}; return actions={returning.DoNow.Count}");
        });
        await Check("WFCD item intelligence", async () =>
        {
            var count = await s.Intelligence.SyncGameItemsAsync(force: true);
            var hits = s.Intelligence.Search("Saryn spores build", 8);
            if (hits.Count == 0) throw new InvalidOperationException("retrieval returned no matches");
            return $"{count} items; query returned {hits.Count} source-stamped matches";
        });
        await Check("official drop intelligence", async () =>
        {
            var count = await s.Intelligence.SyncOfficialDropsAsync(force: true);
            var hits = s.Intelligence.Search("where get Ash Chassis Blueprint", 12);
            if (!hits.Any(h => h.Source == "official-drop"))
                throw new InvalidOperationException("official drop retrieval returned no matching record");
            return $"{count} flattened official drop records; acquisition retrieval verified";
        });
        await Check("world state", async () =>
        {
            await s.WorldState.RefreshAsync();
            var ws = s.WorldState.Current ?? throw new InvalidOperationException(s.WorldState.LastError ?? "no data");
            return $"{ws.Fissures.Count} fissures, sortie={ws.Sortie?.Boss}, cetus={ws.CetusCycle?.State}";
        });
        await Check("market catalog", async () => $"{await s.Market.EnsureItemCatalogAsync()} tradable items");
        await Check("market item details", async () =>
        {
            var item = await s.Market.GetItemDetailsAsync("loki_prime_set")
                       ?? throw new InvalidOperationException("item details returned null");
            if (!item.ItemName.Contains("Loki Prime", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"unexpected item name: {item.ItemName}");
            return $"{item.ItemName}; MR {item.MasteryLevel?.ToString() ?? "n/a"}; tax {item.TradingTax?.ToString() ?? "n/a"}";
        });
        await Check("price snapshot", async () =>
        {
            var snap = await s.Market.GetPriceSnapshotAsync(force: true);
            var priced = snap.Values.Where(p => p.WaPrice > 0 || p.Median > 0).OrderByDescending(p => p.WaPrice).ToList();
            if (priced.Count == 0)
                return $"{snap.Count} v2 item/Ducat rows; bulk price enrichment unavailable, named live orders remain enabled";
            var sample = priced[0];
            return $"{priced.Count} bulk prices; top: {sample.ItemName} ~{sample.WaPrice:0}p/{sample.Ducats}d";
        });
        await Check("relic data", async () => $"{(await s.RelicData.GetRelicsAsync()).Count} relic entries");
        await Check("relic EV ranking", async () =>
        {
            var ranked = await s.RelicData.GetValuedRelicsAsync("Intact", s.Market.LookupPrice);
            var top = ranked.First();
            return $"{ranked.Count} intact relics; best: {top.DisplayName} EV {top.ExpectedPlat:0.0}p (top drop {top.BestItem} {top.BestPlat:0}p)";
        });
        await Check("mastery catalog", async () => $"{await s.Mastery.SyncCatalogAsync()} items synced");
        await Check("riven weapon list", async () => $"{(await s.Market.GetRivenWeaponsAsync()).Count} weapons");
        await CheckOptional("riven auctions (soma)", async () =>
        {
            var auctions = await s.Market.SearchRivenAuctionsAsync("soma");
            if (auctions.Count == 0) return "0 active auctions (endpoint and response shape verified)";
            var buyouts = auctions.Where(a => a.BuyoutPrice is > 0).Select(a => a.BuyoutPrice!.Value).ToList();
            return buyouts.Count > 0
                ? $"{auctions.Count} auctions, cheapest buyout {buyouts.Min()}p"
                : $"{auctions.Count} auctions, no direct buyouts currently";
        });
        await Check("item orders + cached live metrics (loki prime set)", async () =>
        {
            var orders = await s.Market.GetOrdersAsync("loki_prime_set");
            var metric = s.Market.GetLiveMetric("loki_prime_set")
                         ?? throw new InvalidOperationException("live metric row not persisted");
            return $"{orders.Count} orders; sell {metric.LowestSell:0}p / buy {metric.HighestBuy:0}p";
        });
        await CheckOptional("item statistics (loki prime set)", async () =>
        {
            var stats = await s.Market.GetStatisticsAsync("loki_prime_set");
            return $"{stats.Count} daily points, latest median {stats.LastOrDefault()?.Median:0}p";
        });
        await Check("user orders (public slug)", async () =>
        {
            var slug = s.Settings.MarketUsername;
            if (string.IsNullOrWhiteSpace(slug))
                return "skipped (configure a warframe.market username to test personal listings)";
            var orders = await s.Market.GetUserOrdersAsync(slug);
            var named = orders.Count(o => !string.IsNullOrWhiteSpace(o.ItemName) && !o.ItemName.All(char.IsLetterOrDigit));
            var sample = orders.FirstOrDefault();
            return $"{orders.Count} orders, {named} name-resolved; e.g. {sample?.Direction} {sample?.ItemName} @ {sample?.Platinum}p";
        });
        await Check("data source health", () =>
        {
            var sources = s.Intelligence.GetSourceHealth();
            if (sources.Count < 5) throw new InvalidOperationException("missing source-state rows");
            return Task.FromResult(string.Join(", ", sources.Select(x => $"{x.DisplayName}={x.Status}/{x.ItemCount}")));
        });
        await Check("world timers (activation/expiry parse)", () =>
        {
            var ws = s.WorldState.Current ?? throw new InvalidOperationException("no world state");
            var sortie = ws.Sortie?.TimeLeft ?? "n/a";
            var baro = ws.VoidTrader?.StatusLine ?? "n/a";
            if (sortie is "—" or "expired") throw new InvalidOperationException($"sortie countdown not computed ({sortie})");
            return Task.FromResult($"sortie ends in {sortie}; {baro}");
        });

        var dir = Path.Combine(Db.AppDataDir, "logs");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "verify.log"), report.ToString());
        Environment.Exit(failures);
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogCrash(e.Exception, _smokeMode ? "smoke-dispatcher" : "dispatcher");
        e.Handled = true;

        // Automated release verification must fail closed. Showing a modal dialog and
        // continuing previously let a broken XAML load return exit code 0 and package
        // an unusable build. Normal interactive runs still show the operator-friendly dialog.
        if (_smokeMode)
        {
            Shutdown(2);
            return;
        }

        MessageBox.Show(e.Exception.Message, "Cephalon Cuda — Operator, something broke",
            MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static void LogCrash(Exception? ex, string source)
    {
        try
        {
            var dir = Path.Combine(Db.AppDataDir, "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"),
                $"[{DateTimeOffset.Now:u}] ({source}) {ex}\n\n");
        }
        catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        Services?.Dispose();
        base.OnExit(e);
    }
}
