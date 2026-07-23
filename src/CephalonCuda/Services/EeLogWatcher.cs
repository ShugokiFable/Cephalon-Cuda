using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using CephalonCuda.Models;

namespace CephalonCuda.Services;

/// <summary>
/// Read-only tail of %LocalAppData%\Warframe\EE.log (anti-cheat safe: no memory access, no injection).
/// FileSystemWatcher is unreliable for append-only logs, so this polls every 500ms with a persisted offset.
/// Log line signatures drift with game updates — all patterns live in <see cref="Patterns"/> for easy tuning.
/// </summary>
public sealed partial class EeLogWatcher(SettingsService settings) : IDisposable
{
    private CancellationTokenSource? _cts;
    private long _offset;
    private string _remainder = "";
    private readonly List<EeLogEvent> _recent = [];
    private readonly object _lock = new();

    public bool FileExists => File.Exists(settings.EeLogPath);
    public DateTimeOffset? LastActivity { get; private set; }
    public string? CurrentNode { get; private set; }
    public string? PlayerName { get; private set; }
    public bool MissionActive { get; private set; }

    public event Action<EeLogEvent>? EventParsed;
    /// <summary>Fires when the relic reward-choice screen appears — triggers the overlay OCR scan.</summary>
    public event Action? RelicRewardScreen;

    // (kind, regex) — first match wins. Patterns are deliberately loose; DE renames lua files across updates.
    private static readonly (string Kind, Regex Rx)[] Patterns =
    [
        ("Login",        new Regex(@"Sys \[Info\]: Logged in (\S+)", RegexOptions.Compiled)),
        ("RelicScreen",  new Regex(@"ProjectionRewardChoice", RegexOptions.Compiled)),
        ("MissionStart", new Regex(@"(ThemedSquadOverlay\.lua: Host loading|Sys \[Info\]: Mission name:|LotusProfileData|OnMissionStart)", RegexOptions.Compiled)),
        ("MissionEnd",   new Regex(@"(EndOfMatch\.lua|EOM\.lua|MissionSuccess|CommitInventoryChangesToDB)", RegexOptions.Compiled)),
        ("NodeLoad",     new Regex(@"Script \[Info\]: ThemedSquadOverlay\.lua: Mission \[(?<node>[^\]]+)\]", RegexOptions.Compiled)),
        ("Foundry",      new Regex(@"(FoundryReward|ClaimCompletedRecipe)", RegexOptions.Compiled)),
        ("Trade",        new Regex(@"(TradingConfirm|FinishedTrading)", RegexOptions.Compiled)),
    ];

    public void Start()
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => LoopAsync(_cts.Token));
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        // Skip history on first attach: only parse lines written while we're running.
        try { if (FileExists) _offset = new FileInfo(settings.EeLogPath).Length; } catch { }

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        while (await SafeWaitAsync(timer, ct))
        {
            try { Poll(); }
            catch { /* file mid-rotation or locked exclusively; retry next tick */ }
        }
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer t, CancellationToken ct)
    {
        try { return await t.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }

    private void Poll()
    {
        var path = settings.EeLogPath;
        if (!File.Exists(path)) return;

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (fs.Length < _offset)
        {
            // Game restarted: EE.log was recreated.
            _offset = 0;
            _remainder = "";
            MissionActive = false;
            Emit("Session", "New game session detected (EE.log reset)");
        }
        if (fs.Length == _offset) return;

        fs.Seek(_offset, SeekOrigin.Begin);
        var buf = new byte[fs.Length - _offset];
        var read = fs.Read(buf, 0, buf.Length);
        _offset += read;
        LastActivity = DateTimeOffset.Now;

        var text = _remainder + Encoding.UTF8.GetString(buf, 0, read);
        var lines = text.Split('\n');
        _remainder = lines[^1]; // partial trailing line
        for (int i = 0; i < lines.Length - 1; i++)
            ParseLine(lines[i].TrimEnd('\r'));
    }

    private void ParseLine(string line)
    {
        if (line.Length == 0) return;
        foreach (var (kind, rx) in Patterns)
        {
            var m = rx.Match(line);
            if (!m.Success) continue;

            switch (kind)
            {
                case "Login":
                    PlayerName = m.Groups[1].Value;
                    Emit(kind, $"Logged in as {PlayerName}");
                    break;
                case "RelicScreen":
                    Emit(kind, "Relic reward selection screen");
                    RelicRewardScreen?.Invoke();
                    break;
                case "MissionStart":
                    MissionActive = true;
                    Emit(kind, "Mission started");
                    break;
                case "MissionEnd":
                    MissionActive = false;
                    Emit(kind, "Mission ended");
                    break;
                case "NodeLoad":
                    CurrentNode = m.Groups["node"].Value;
                    Emit(kind, $"Loading {CurrentNode}");
                    break;
                default:
                    Emit(kind, Truncate(line, 160));
                    break;
            }
            return;
        }
    }

    private void Emit(string kind, string detail)
    {
        var ev = new EeLogEvent { Kind = kind, Detail = detail };
        lock (_lock)
        {
            _recent.Add(ev);
            if (_recent.Count > 300) _recent.RemoveRange(0, _recent.Count - 300);
        }
        EventParsed?.Invoke(ev);
    }

    public List<EeLogEvent> RecentEvents(int max = 50)
    {
        lock (_lock) return _recent.TakeLast(max).ToList();
    }

    private static string Truncate(string s, int len) => s.Length <= len ? s : s[..len] + "…";

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
