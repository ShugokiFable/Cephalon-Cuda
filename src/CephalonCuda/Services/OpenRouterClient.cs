using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CephalonCuda.Models;

namespace CephalonCuda.Services;

/// <summary>
/// OpenRouter chat-completions client. Streaming SSE for the advisor panel,
/// non-streaming + SQLite response cache for background jobs.
/// </summary>
public sealed class OpenRouterClient(HttpClient http, SettingsService settings, Db db)
{
    private const string Endpoint = "https://openrouter.ai/api/v1/chat/completions";

    private static object BuildMessage(ChatMsg m) => m.ImageBase64Png is null
        ? new { role = m.Role, content = (object)m.Content }
        : new
        {
            role = m.Role,
            content = (object)new object[]
            {
                new { type = "text", text = m.Content },
                new { type = "image_url", image_url = new { url = $"data:image/png;base64,{m.ImageBase64Png}" } },
            },
        };

    private HttpRequestMessage BuildRequest(IEnumerable<ChatMsg> messages, string model, bool stream)
    {
        var key = settings.ApiKey
            ?? throw new InvalidOperationException("No OpenRouter API key set. Add one in Settings.");
        var payload = JsonSerializer.Serialize(new
        {
            model,
            stream,
            messages = messages.Select(BuildMessage).ToArray(),
        });
        var req = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        req.Headers.Add("HTTP-Referer", "https://cephalon-cuda.local");
        req.Headers.Add("X-Title", "Cephalon Cuda");
        return req;
    }

    /// <summary>Streaming completion; yields content deltas as they arrive.</summary>
    public async IAsyncEnumerable<string> StreamChatAsync(
        IEnumerable<ChatMsg> messages, string model, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var req = BuildRequest(messages, model, stream: true);
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"OpenRouter {(int)resp.StatusCode}: {Truncate(body, 400)}");
        }

        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            var data = line[6..].Trim();
            if (data == "[DONE]") break;

            string? delta = null;
            try
            {
                using var doc = JsonDocument.Parse(data);
                if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                {
                    var c0 = choices[0];
                    if (c0.TryGetProperty("delta", out var d) && d.TryGetProperty("content", out var content))
                        delta = content.GetString();
                }
            }
            catch (JsonException) { /* keep-alive comment or partial chunk */ }
            if (!string.IsNullOrEmpty(delta)) yield return delta;
        }
    }

    /// <summary>Non-streaming completion with optional local cache (reduces API cost for repeated background queries).</summary>
    public async Task<string> CompleteAsync(
        IEnumerable<ChatMsg> messages, string model, TimeSpan? cacheTtl = null, CancellationToken ct = default)
    {
        var msgList = messages.ToList();
        string? hash = null;
        if (cacheTtl is not null)
        {
            var keyText = model + "" + string.Join("", msgList.Select(m => m.Role + ":" + m.Content));
            hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(keyText)));
            var cached = db.Query(
                "SELECT response, created_at FROM ai_cache WHERE hash=@h",
                r => (Response: r.GetString(0), CreatedAt: DateTimeOffset.Parse(r.GetString(1))),
                ("@h", hash)).FirstOrDefault();
            if (cached.Response is not null && DateTimeOffset.UtcNow - cached.CreatedAt < cacheTtl)
                return cached.Response;
        }

        using var req = BuildRequest(msgList, model, stream: false);
        using var resp = await http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"OpenRouter {(int)resp.StatusCode}: {Truncate(body, 400)}");

        using var doc = JsonDocument.Parse(body);
        var text = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";

        if (hash is not null)
            db.Exec("INSERT INTO ai_cache(hash,response,created_at) VALUES(@h,@r,@c) " +
                    "ON CONFLICT(hash) DO UPDATE SET response=@r, created_at=@c",
                ("@h", hash), ("@r", text), ("@c", DateTimeOffset.UtcNow.ToString("O")));
        return text;
    }

    /// <summary>Cheap connectivity probe for the Settings "Test key" button.</summary>
    public async Task<string> TestAsync(CancellationToken ct = default)
    {
        var reply = await CompleteAsync(
            [new ChatMsg { Role = "user", Content = "Reply with exactly: OPERATIONAL" }],
            settings.BackgroundModel, cacheTtl: null, ct);
        return reply.Trim();
    }

    private static string Truncate(string s, int len) => s.Length <= len ? s : s[..len] + "…";
}
