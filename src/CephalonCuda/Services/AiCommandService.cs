using System.Text.Json;
using System.Text.RegularExpressions;

namespace CephalonCuda.Services;

/// <summary>
/// Parses structured "cuda-action" blocks from AI responses and executes them against the local
/// database. The AI emits fenced code blocks tagged <c>```cuda-action</c> containing a JSON array
/// of action objects. This service extracts those blocks, validates the payloads, and applies
/// approved changes after the user confirms via <see cref="Views.AiActionDialog"/>.
/// </summary>
public sealed class AiCommandService(FoundryService foundry, MasteryService mastery, InventoryService inventory)
{
    // Matches ```cuda-action ... ``` blocks in the AI response.
    private static readonly Regex ActionBlockRx = new(
        @"```cuda-action\s*\r?\n([\s\S]*?)\r?\n\s*```", RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>Extract all cuda-action blocks from a response and parse into action objects.</summary>
    public List<AiAction> ParseActions(string response)
    {
        var actions = new List<AiAction>();
        foreach (Match match in ActionBlockRx.Matches(response))
        {
            var json = match.Groups[1].Value.Trim();
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in doc.RootElement.EnumerateArray())
                        TryParseOne(el, actions);
                }
                else if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    TryParseOne(doc.RootElement, actions);
                }
            }
            catch (JsonException) { /* malformed block — skip silently */ }
        }
        return actions;
    }

    private static void TryParseOne(JsonElement el, List<AiAction> actions)
    {
        if (!el.TryGetProperty("action", out var a) || a.ValueKind != JsonValueKind.String) return;
        var actionName = a.GetString();
        if (string.IsNullOrWhiteSpace(actionName)) return;

        var entry = new AiAction { Action = actionName };

        if (el.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
            entry.Name = name.GetString();

        if (el.TryGetProperty("duration_minutes", out var dur) && dur.ValueKind == JsonValueKind.Number)
            entry.DurationMinutes = dur.GetInt32();

        if (el.TryGetProperty("names", out var names) && names.ValueKind == JsonValueKind.Array)
            entry.Names = names.EnumerateArray()
                .Where(n => n.ValueKind == JsonValueKind.String)
                .Select(n => n.GetString()!)
                .Where(n => n.Length > 0)
                .ToList();

        if (el.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            entry.Items = [];
            foreach (var item in items.EnumerateArray())
            {
                var ie = new AiInventoryEntry();
                if (item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                    ie.Name = n.GetString()!;
                if (item.TryGetProperty("quantity", out var q) && q.ValueKind == JsonValueKind.Number)
                    ie.Quantity = q.GetInt32();
                if (item.TryGetProperty("notes", out var notes) && notes.ValueKind == JsonValueKind.String)
                    ie.Notes = notes.GetString();
                if (!string.IsNullOrWhiteSpace(ie.Name))
                    entry.Items.Add(ie);
            }
        }

        actions.Add(entry);
    }

    /// <summary>Execute a batch of approved actions. Returns per-action results.</summary>
    public List<AiActionResult> ExecuteAll(IReadOnlyList<AiAction> actions)
    {
        var results = new List<AiActionResult>();
        foreach (var action in actions)
        {
            try
            {
                results.Add(action.Action switch
                {
                    "add_foundry_job" => ExecuteAddFoundry(action),
                    "claim_foundry_job" => ExecuteClaimFoundry(action),
                    "mark_mastered" => ExecuteMarkMastered(action, true),
                    "unmark_mastered" => ExecuteMarkMastered(action, false),
                    "add_inventory" => ExecuteAddInventory(action),
                    "delete_inventory" => ExecuteDeleteInventory(action),
                    _ => new AiActionResult(false, $"Unknown action: {action.Action}"),
                });
            }
            catch (Exception ex)
            {
                results.Add(new AiActionResult(false, $"{action.Action} failed: {ex.Message}"));
            }
        }
        return results;
    }

    /// <summary>Human-readable description of a proposed action for the confirmation dialog.</summary>
    public static string Describe(AiAction a) => a.Action switch
    {
        "add_foundry_job" => $"⚒ Foundry: {a.Name} ({(a.DurationMinutes ?? 0) / 60.0:0.#}h)",
        "claim_foundry_job" => $"✓ Claim: {a.Name}",
        "mark_mastered" => $"★ Mark mastered: {string.Join(", ", a.Names ?? [])}",
        "unmark_mastered" => $"☆ Unmark mastered: {string.Join(", ", a.Names ?? [])}",
        "add_inventory" => $"📦 Inventory: {string.Join(", ", (a.Items ?? []).Select(i => $"{i.Name}×{i.Quantity}"))}",
        "delete_inventory" => $"✕ Remove: {string.Join(", ", a.Names ?? [])}",
        _ => a.Action,
    };

    // ---- individual executors ------------------------------------------------

    private AiActionResult ExecuteAddFoundry(AiAction a)
    {
        if (a.Name is not { Length: > 0 }) return new(false, "add_foundry_job: missing name.");
        if (a.DurationMinutes is not > 0) return new(false, $"add_foundry_job: bad duration for '{a.Name}'.");
        foundry.Add(a.Name, a.DurationMinutes.Value);
        return new(true, $"Added foundry job: {a.Name} ({a.DurationMinutes.Value / 60.0:0.#}h)");
    }

    private AiActionResult ExecuteClaimFoundry(AiAction a)
    {
        if (a.Name is not { Length: > 0 }) return new(false, "claim_foundry_job: missing name.");
        var jobs = foundry.GetAll(includeClaimed: false);
        var match = jobs.FirstOrDefault(j => j.Name.Contains(a.Name, StringComparison.OrdinalIgnoreCase));
        if (match is null) return new(false, $"No active job matching '{a.Name}'.");
        foundry.Claim(match.Id);
        return new(true, $"Claimed: {match.Name}");
    }

    private AiActionResult ExecuteMarkMastered(AiAction a, bool mastered)
    {
        if (a.Names is not { Count: > 0 })
            return new(false, $"{(mastered ? "mark" : "unmark")}_mastered: missing names.");
        var allItems = mastery.GetAll().ToDictionary(i => i.Name, StringComparer.OrdinalIgnoreCase);
        var matched = new List<string>();
        var missed = new List<string>();
        foreach (var name in a.Names)
        {
            if (allItems.ContainsKey(name))
            {
                mastery.SetMastered(name, mastered);
                matched.Add(name);
            }
            else missed.Add(name);
        }
        var verb = mastered ? "Marked mastered" : "Unmarked";
        var msg = $"{verb}: {string.Join(", ", matched)}";
        if (missed.Count > 0) msg += $" (not in catalog: {string.Join(", ", missed)})";
        return new(matched.Count > 0, msg);
    }

    private AiActionResult ExecuteAddInventory(AiAction a)
    {
        if (a.Items is not { Count: > 0 }) return new(false, "add_inventory: missing items.");
        foreach (var item in a.Items) inventory.Upsert(item.Name, Math.Max(1, item.Quantity), item.Notes);
        return new(true, $"Inventory: {string.Join(", ", a.Items.Select(i => $"{i.Name}×{i.Quantity}"))}");
    }

    private AiActionResult ExecuteDeleteInventory(AiAction a)
    {
        if (a.Names is not { Count: > 0 }) return new(false, "delete_inventory: missing names.");
        var deleted = a.Names.Count(n => inventory.DeleteByName(n));
        return new(deleted > 0, $"Removed {deleted} inventory item(s).");
    }
}

// ---- data types -------------------------------------------------------------

public sealed class AiAction
{
    public string Action { get; set; } = "";
    public string? Name { get; set; }
    public int? DurationMinutes { get; set; }
    public List<string>? Names { get; set; }
    public List<AiInventoryEntry>? Items { get; set; }
}

public sealed class AiInventoryEntry
{
    public string Name { get; set; } = "";
    public int Quantity { get; set; } = 1;
    public string? Notes { get; set; }
}

public sealed record AiActionResult(bool Success, string Message);