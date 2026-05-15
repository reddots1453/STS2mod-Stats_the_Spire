using System.Linq;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CommunityStats.Config;
using CommunityStats.UI;
using CommunityStats.Util;

namespace CommunityStats.Api;

/// <summary>
/// HttpClient wrapper for all server communication.
/// All traffic is forced over HTTPS. The AllowHttp config flag exists
/// as a GFW workaround (SNI-blocking of some domains); when enabled,
/// an HTTP base URL is used as-is instead of being upgraded.
/// </summary>
public sealed class ApiClient : IDisposable
{
    public static ApiClient Instance { get; } = new();

    private readonly HttpClient _queryClient;
    private readonly HttpClient _uploadClient;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private ApiClient()
    {
        // BaseAddress MUST end with '/' for relative URL resolution to work correctly.
        var baseUrl = ModConfig.ApiBaseUrl.TrimEnd('/') + "/";

        // Force HTTPS unless user explicitly opted into HTTP (GFW workaround).
        if (!ModConfig.AllowHttp
            && baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            baseUrl = baseUrl.Replace("http://", "https://");
            Safe.Warn($"[ApiClient] Upgraded base URL to HTTPS: {baseUrl}");
        }

        _queryClient = new HttpClient
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromMilliseconds(ModConfig.QueryTimeoutMs)
        };
        _queryClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        _queryClient.DefaultRequestHeaders.Add("X-Mod-Version", ModConfig.ModVersion);
        _queryClient.DefaultRequestHeaders.Add("User-Agent", $"StatsTheSpire/{ModConfig.ModVersion}");

        _uploadClient = new HttpClient
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromMilliseconds(ModConfig.UploadTimeoutMs)
        };
        _uploadClient.DefaultRequestHeaders.Add("X-Mod-Version", ModConfig.ModVersion);
        _uploadClient.DefaultRequestHeaders.Add("User-Agent", $"StatsTheSpire/{ModConfig.ModVersion}");
    }

    // ── Upload ──────────────────────────────────────────────

    /// <summary>
    /// Uploads a completed run. On failure, queues for offline retry.
    /// </summary>
    public async Task UploadRunAsync(RunUploadPayload payload)
    {
        var json = JsonSerializer.Serialize(payload, _jsonOptions);
        var status = await PostJsonWithStatusAsync("runs", json);

        if (status >= 200 && status < 300)
        {
            Safe.Info("Run uploaded successfully");
            UploadNotice.Show(L.Get("upload.run_success"));
            // Drain any pending offline uploads while we have connectivity
            Safe.RunAsync(() => OfflineQueue.DrainAsync(rawJson => PostJsonWithStatusAsync("runs", rawJson)));
        }
        else if (status >= 400 && status < 500 && status != 429)
        {
            // Permanent client error (schema mismatch, validation, etc.).
            // Queueing is pointless — every retry will fail identically and
            // block newer runs behind a poison payload. Drop and surface.
            Safe.Warn($"Run upload rejected (HTTP {status}); not queueing");
            UploadNotice.Show(L.Get("upload.run_rejected"));
        }
        else
        {
            // Network error (status=0), 5xx, or 429: transient — queue for retry
            OfflineQueue.Enqueue(payload);
            UploadNotice.Show(L.Get("upload.run_queued"));
        }
    }

    /// <summary>
    /// Posts raw JSON to an endpoint. Returns the HTTP status code,
    /// or 0 on network error. Callers inspect the code to distinguish
    /// success (2xx), rate limit (429), validation failure (422) etc.
    /// </summary>
    public async Task<int> PostJsonWithStatusAsync(string endpoint, string json)
    {
        try
        {
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _uploadClient.PostAsync(endpoint, content);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                var truncated = body.Length > 200 ? body[..200] + "..." : body;
                Safe.Warn($"Upload failed: {(int)response.StatusCode} — {truncated}");
            }
            return (int)response.StatusCode;
        }
        catch (Exception ex)
        {
            Safe.Warn($"Upload error: {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// Posts raw JSON to an endpoint. Returns true on 2xx.
    /// </summary>
    public async Task<bool> PostJsonAsync(string endpoint, string json)
    {
        var code = await PostJsonWithStatusAsync(endpoint, json);
        return code >= 200 && code < 300;
    }

    // ── Bulk Query ──────────────────────────────────────────

    /// <summary>
    /// Fetches the pre-computed bulk stats bundle for a character + filter.
    /// </summary>
    public async Task<BulkStatsBundle?> GetBulkStatsAsync(string character, FilterSettings filter)
    {
        var version = VersionManager.GetEffectiveVersion(filter);
        var branch = BranchManager.GetEffectiveBranch(filter);
        // filter.ToQueryString() already emits char= from ResolveCharacter(); strip
        // it so we don't send the parameter twice (server takes the first, but some
        // intermediaries reject the dup).
        var qs = filter.ToQueryString();
        var aux = qs.Length > 0 ? qs[1..] : "";
        var auxParts = aux.Length > 0
            ? aux.Split('&').Where(p => !p.StartsWith("char=", StringComparison.Ordinal)
                                     && !p.StartsWith("ver=", StringComparison.Ordinal)
                                     && !p.StartsWith("branch=", StringComparison.Ordinal))
            : System.Array.Empty<string>();
        var auxJoined = string.Join("&", auxParts);
        var url = $"stats/bulk?char={Uri.EscapeDataString(character)}&ver={Uri.EscapeDataString(version)}"
                + $"&branch={Uri.EscapeDataString(branch)}"
                + (auxJoined.Length > 0 ? "&" + auxJoined : "");

        return await GetAsync<BulkStatsBundle>(url);
    }

    // ── On-Demand Fallback Queries ──────────────────────────

    public async Task<Dictionary<string, CardStats>?> GetCardStatsBatchAsync(
        IEnumerable<string> cardIds, FilterSettings filter)
    {
        var ids = string.Join(",", cardIds.Select(Uri.EscapeDataString));
        var qs = filter.ToQueryString();
        var url = $"stats/cards?cards={ids}{(qs.Length > 0 ? "&" + qs[1..] : "")}";
        return await GetAsync<Dictionary<string, CardStats>>(url);
    }

    public async Task<Dictionary<string, RelicStats>?> GetRelicStatsBatchAsync(
        IEnumerable<string> relicIds, FilterSettings filter)
    {
        var ids = string.Join(",", relicIds.Select(Uri.EscapeDataString));
        var qs = filter.ToQueryString();
        var url = $"stats/relics?relics={ids}{(qs.Length > 0 ? "&" + qs[1..] : "")}";
        return await GetAsync<Dictionary<string, RelicStats>>(url);
    }

    public async Task<EventStats?> GetEventStatsAsync(string eventId, FilterSettings filter)
    {
        var qs = filter.ToQueryString();
        var url = $"stats/events/{Uri.EscapeDataString(eventId)}{qs}";
        return await GetAsync<EventStats>(url);
    }

    public async Task<Dictionary<string, EncounterStats>?> GetEncounterStatsBatchAsync(
        IEnumerable<string> encounterIds, FilterSettings filter)
    {
        var ids = string.Join(",", encounterIds.Select(Uri.EscapeDataString));
        var qs = filter.ToQueryString();
        var url = $"stats/encounters?ids={ids}{(qs.Length > 0 ? "&" + qs[1..] : "")}";
        return await GetAsync<Dictionary<string, EncounterStats>>(url);
    }

    public async Task<List<string>?> GetAvailableVersionsAsync()
    {
        return await GetAsync<List<string>>("meta/versions");
    }

    // ── Internal ────────────────────────────────────────────

    private async Task<T?> GetAsync<T>(string url) where T : class
    {
        try
        {
            using var response = await _queryClient.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                Safe.Warn($"Query failed: GET {url} → {(int)response.StatusCode}");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<T>(json, _jsonOptions);
        }
        catch (TaskCanceledException)
        {
            Safe.Warn($"Query timeout: GET {url}");
            return null;
        }
        catch (Exception ex)
        {
            Safe.Warn($"Query error: GET {url} → {ex.GetType().Name}: {ex.Message}");
            var inner = ex.InnerException;
            int depth = 0;
            while (inner != null && depth < 5)
            {
                Safe.Warn($"  [Inner{depth}] {inner.GetType().FullName}: {inner.Message}");
                inner = inner.InnerException;
                depth++;
            }
            return null;
        }
    }

    public void Dispose()
    {
        _queryClient.Dispose();
        _uploadClient.Dispose();
    }
}
