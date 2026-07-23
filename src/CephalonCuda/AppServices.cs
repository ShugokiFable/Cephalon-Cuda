using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using CephalonCuda.Services;

namespace CephalonCuda;

/// <summary>Composition root. Everything is a singleton wired here; no game memory is ever touched.</summary>
public sealed class AppServices : IDisposable
{
    private readonly CancellationTokenSource _refreshLoopCts = new();
    private Task? _refreshTask;
    private bool _started;
    public HttpClient Http { get; }
    public HttpClient AiHttp { get; }
    public Db Db { get; }
    public SettingsService Settings { get; }
    public WorldStateService WorldState { get; }
    public MarketService Market { get; }
    public RelicDataService RelicData { get; }
    public DataIntelligenceService Intelligence { get; }
    public EeLogWatcher EeLog { get; }
    public OpenRouterClient OpenRouter { get; }
    public ContextAggregator Context { get; }
    public InventoryService Inventory { get; }
    public FoundryService Foundry { get; }
    public MasteryService Mastery { get; }
    public GameWindowTracker Tracker { get; }
    public ScreenCaptureService Capture { get; }
    public OcrService Ocr { get; }
    public RelicRewardScanner Scanner { get; }
    public AiCommandService AiCommands { get; }
    public OnboardingService Onboarding { get; }
    public ReturnPlannerService Returns { get; }

    /// <summary>Raised by overlay/screen-capture flows so the Advisor tab can show a preview thumbnail.</summary>
    public event Action<string /*base64Png*/, string /*label*/>? ScreenCaptured;
    public void RaiseScreenCaptured(string base64Png, string label) => ScreenCaptured?.Invoke(base64Png, label);

    /// <summary>Raised when the Advisor tab clears chat history so the overlay chat can clear too.</summary>
    public event Action? ChatCleared;
    public event Action<string>? AdvisorPromptRequested;
    public void RequestAdvisorPrompt(string prompt) => AdvisorPromptRequested?.Invoke(prompt);
    public void RaiseChatCleared() => ChatCleared?.Invoke();

    /// <summary>Raised after a chat message is persisted to DB so both Advisor and overlay stay in sync.</summary>
    public event Action<string /*role*/, string /*content*/>? ChatMessagePersisted;
    public void RaiseChatMessagePersisted(string role, string content) => ChatMessagePersisted?.Invoke(role, content);

    /// <summary>Insert a chat row without broadcasting it — for turns a surface wants in history but not live-mirrored elsewhere (e.g. the F11 analysis panel result when chat isn't open).</summary>
    public void InsertChatMessage(string role, string content) =>
        Db.Exec("INSERT INTO chat_messages(role,content,created_at) VALUES(@r,@c,@t)",
            ("@r", role), ("@c", content), ("@t", DateTimeOffset.UtcNow.ToString("O")));

    /// <summary>Insert a chat row and broadcast it so every open chat surface (Advisor tab, overlay panel) mirrors it live.</summary>
    public void PersistChatMessage(string role, string content)
    {
        InsertChatMessage(role, content);
        RaiseChatMessagePersisted(role, content);
    }

    /// <summary>Most recent chat rows, oldest-first.</summary>
    public List<(string Role, string Content)> LoadRecentChatHistory(int limit = 40)
    {
        var rows = Db.Query(
            "SELECT role, content FROM chat_messages ORDER BY id DESC LIMIT @lim",
            r => (Role: r.GetString(0), Content: r.GetString(1)),
            ("@lim", limit));
        rows.Reverse();
        return rows;
    }

    public AppServices()
    {
        Http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            ConnectTimeout = TimeSpan.FromSeconds(15),
            MaxConnectionsPerServer = 8,
        });
        Http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CephalonCuda", "2.2.0"));
        Http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        Http.Timeout = TimeSpan.FromSeconds(45);

        // Separate client for AI streaming: SSE responses can stay open for minutes.
        AiHttp = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

        Db = new Db();
        Settings = new SettingsService(Db);
        WorldState = new WorldStateService(Http);
        Market = new MarketService(Http, Db);
        RelicData = new RelicDataService(Http);
        Intelligence = new DataIntelligenceService(Http, Db, Market);
        EeLog = new EeLogWatcher(Settings);
        OpenRouter = new OpenRouterClient(AiHttp, Settings, Db);
        Onboarding = new OnboardingService(Db);
        Returns = new ReturnPlannerService(Settings, Db, Onboarding, WorldState, Intelligence, EeLog);
        Context = new ContextAggregator(Settings, Db, WorldState, EeLog, Market, Intelligence, Onboarding, Returns);
        Inventory = new InventoryService(Db, Market);
        Foundry = new FoundryService(Db);
        Mastery = new MasteryService(Http, Db);
        Tracker = new GameWindowTracker();
        Capture = new ScreenCaptureService();
        Ocr = new OcrService();
        Scanner = new RelicRewardScanner(Tracker, Capture, Ocr, Market, RelicData, Context);
        AiCommands = new AiCommandService(Foundry, Mastery, Inventory);
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        WorldState.Start();
        EeLog.Start();
        Tracker.Start();
        _refreshTask = Task.Run(() => RunDataRefreshLoopAsync(_refreshLoopCts.Token));
    }

    private async Task RunDataRefreshLoopAsync(CancellationToken ct)
    {
        DateTimeOffset nextCommunityRefresh = DateTimeOffset.MinValue;
        DateTimeOffset nextMasteryRefresh = DateTimeOffset.MinValue;
        DateTimeOffset nextRelicRefresh = DateTimeOffset.MinValue;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (Settings.AutoRefreshData)
                {
                    try { await Intelligence.RefreshAllAsync(ct: ct); }
                    catch when (!ct.IsCancellationRequested) { }

                    if (Settings.CommunityFeedUrl is { Length: > 0 } feed &&
                        DateTimeOffset.UtcNow >= nextCommunityRefresh)
                    {
                        try
                        {
                            await Intelligence.RefreshCommunityFeedAsync(feed, ct);
                            nextCommunityRefresh = DateTimeOffset.UtcNow.AddHours(12);
                        }
                        catch when (!ct.IsCancellationRequested)
                        {
                            nextCommunityRefresh = DateTimeOffset.UtcNow.AddHours(1);
                        }
                    }

                    if (DateTimeOffset.UtcNow >= nextRelicRefresh)
                    {
                        try
                        {
                            await RelicData.GetRelicsAsync(ct);
                            nextRelicRefresh = DateTimeOffset.UtcNow.AddHours(6);
                        }
                        catch when (!ct.IsCancellationRequested)
                        {
                            nextRelicRefresh = DateTimeOffset.UtcNow.AddMinutes(30);
                        }
                    }

                    if (DateTimeOffset.UtcNow >= nextMasteryRefresh)
                    {
                        try
                        {
                            await Mastery.SyncCatalogAsync(ct);
                            nextMasteryRefresh = DateTimeOffset.UtcNow.AddHours(24);
                        }
                        catch when (!ct.IsCancellationRequested)
                        {
                            nextMasteryRefresh = DateTimeOffset.UtcNow.AddHours(1);
                        }
                    }

                    if (Settings.MarketUsername is { Length: > 0 } slug)
                    {
                        try { await Market.GetUserOrdersAsync(slug, ct); }
                        catch when (!ct.IsCancellationRequested) { }
                    }
                }

                // Read the setting every cycle so cadence changes take effect without an app restart.
                await Task.Delay(TimeSpan.FromMinutes(Settings.DataRefreshMinutes), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    public void Dispose()
    {
        _refreshLoopCts.Cancel();
        try { _refreshTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _refreshLoopCts.Dispose();
        WorldState.Dispose();
        EeLog.Dispose();
        Tracker.Dispose();
        Capture.Dispose();
        Http.Dispose();
        AiHttp.Dispose();
    }
}
