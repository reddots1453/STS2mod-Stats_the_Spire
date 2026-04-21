using System.Text.Json;
using CommunityStats.Collection;
using CommunityStats.Config;
using CommunityStats.Util;

namespace CommunityStats.Api;

/// <summary>
/// Central data distributor: bulk preload at Run start, cache-first lookups,
/// on-demand HTTP fallback when cache misses.
/// </summary>
public sealed class StatsProvider
{
    public static StatsProvider Instance { get; } = new();

    private BulkStatsBundle? _bundle;
    private FilterSettings? _bundleFilter;
    private volatile bool _isPreloading;

    public bool IsPreloading => _isPreloading;
    public bool HasBundle => _bundle != null || ModConfig.CurrentFilter.MyDataOnly;
    public int TotalRunCount => ModConfig.CurrentFilter.MyDataOnly
        ? RunHistoryAnalyzer.Instance.LocalCards.TotalRuns
        : (_bundle?.TotalRuns ?? 0);

    /// <summary>
    /// PRD §3.13: fires after bundle data changes (filter apply, preload
    /// complete, MyDataOnly toggle). Subscribers re-render their currently
    /// visible stats labels. Always dispatched via scene-tree CallDeferred
    /// so handlers run on the Godot main thread.
    /// </summary>
    public static event Action? DataRefreshed;

    /// <summary>
    /// Invoke DataRefreshed on the Godot main thread. Safe to call from
    /// any thread — marshals via Callable.CallDeferred.
    /// </summary>
    public static void FireDataRefreshed()
    {
        var handlers = DataRefreshed;
        if (handlers == null) return;

        Godot.Callable.From(() =>
        {
            Safe.Run(() => handlers.Invoke());
        }).CallDeferred();
    }

    /// <summary>
    /// Load bundled test data immediately so stats are available before any async preload.
    /// Called once at mod init.
    /// </summary>
    public void EnsureTestDataLoaded()
    {
        if (_bundle != null) return;
        var testBundle = LoadBundledTestData();
        if (testBundle != null)
        {
            _bundle = testBundle;
            Safe.Info($"Loaded bundled test data: {testBundle.Cards.Count} cards, {testBundle.Relics.Count} relics, {testBundle.Events.Count} events");
        }
    }

    // ── Preload (Run start) ─────────────────────────────────

    /// <summary>
    /// Called at Run start. Fetches bulk stats for the character + current filter.
    /// Falls back to disk cache on network failure.
    /// </summary>
    public async Task PreloadForRunAsync(string character, FilterSettings filter)
    {
        _isPreloading = true;
        _bundleFilter = filter;
        var diskKey = $"bulk_{character}_{filter.Hash()}";

        try
        {
            var bundle = await ApiClient.Instance.GetBulkStatsAsync(character, filter);
            if (bundle != null)
            {
                _bundle = bundle;
                StatsCache.Instance.Set(diskKey, bundle);
                StatsCache.Instance.WriteDisk(diskKey, bundle);
                Safe.Info($"Preloaded bulk stats for {character} ({_bundle.Cards.Count} cards, {_bundle.Relics.Count} relics, {_bundle.Events.Count} events, {_bundle.Encounters.Count} encounters)");
                _isPreloading = false;
                return;
            }
        }
        catch (Exception ex)
        {
            Safe.Warn($"Preload network error: {ex.GetType().Name}: {ex.Message}");
            if (ex.InnerException != null)
                Safe.Warn($"  InnerException: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
        }

        // Network failed → try disk cache
        var cached = StatsCache.Instance.ReadDisk<BulkStatsBundle>(diskKey);
        if (cached != null)
        {
            _bundle = cached;
            Safe.Info($"Preload fell back to disk cache for {character}");
            _isPreloading = false;
            return;
        }

        // Disk cache also failed → load bundled test data as fallback
        var testBundle = LoadBundledTestData();
        if (testBundle != null)
        {
            _bundle = testBundle;
            Safe.Info($"Preload fell back to bundled test data ({testBundle.Cards.Count} cards, {testBundle.Relics.Count} relics)");
        }
        else
        {
            Safe.Warn($"No cached or test data available for {character}");
        }

        _isPreloading = false;
    }

    /// <summary>
    /// Clears bundle and re-preloads when filter changes. PRD §3.13:
    /// fires DataRefreshed at the end so currently visible stats labels
    /// can auto-refresh with the new bundle (or with local-only data
    /// when MyDataOnly is on).
    /// </summary>
    public async Task OnFilterChangedAsync(string? character, FilterSettings newFilter)
    {
        _bundle = null;
        StatsCache.Instance.InvalidateAll();

        // Short-circuit when MyDataOnly is on — no server fetch needed.
        if (newFilter.MyDataOnly)
        {
            EnsureTestDataLoaded(); // fallback for non-MyDataOnly lookups outside runs
            FireDataRefreshed();
            return;
        }

        // Main menu / no active run: ResolveCharacter() returns null for
        // "all" and for "auto" outside a run. Still hit the API — pass "all"
        // so the user sees community aggregate data instead of test bundle.
        var charForApi = character;
        if (charForApi == null)
        {
            var mode = newFilter.CharacterFilterMode ?? "auto";
            charForApi = (mode == "all" || mode == "auto") ? "all" : mode;
        }
        await PreloadForRunAsync(charForApi, newFilter);

        FireDataRefreshed();
    }

    /// <summary>
    /// Clears all state at Run end.
    /// </summary>
    public void Reset()
    {
        _bundle = null;
        _bundleFilter = null;
        _isPreloading = false;
    }

    // ── Card Stats ──────────────────────────────────────────

    public CardStats? GetCardStats(string cardId)
    {
        // PRD §3.13 — MyDataOnly: bypass community bundle, read local aggregations.
        if (ModConfig.CurrentFilter.MyDataOnly)
        {
            var local = RunHistoryAnalyzer.Instance.LocalCards.Get(cardId);
            if (local == null || local.RunsWith == 0) return null;
            return new CardStats
            {
                PickRate = local.PickRate,
                WinRate = local.WinRate,
                UpgradeRate = local.UpgradeRate,
                RemovalRate = local.RemovalRate,
                ShopBuyRate = local.BuyRate,
                SampleSize = local.RunsWith,
            };
        }

        if (_bundle?.Cards.TryGetValue(cardId, out var stats) == true)
            return stats;
        return null;
    }

    /// <summary>
    /// Batch lookup: returns stats for each card ID (null if not found).
    /// If bundle covers all, returns immediately. Otherwise falls back to API.
    /// </summary>
    public async Task<Dictionary<string, CardStats?>> GetCardStatsBatchAsync(
        List<string> cardIds, FilterSettings filter)
    {
        var result = new Dictionary<string, CardStats?>();

        // Fast path: all in bundle
        if (_bundle != null && FilterMatches(filter))
        {
            var allHit = true;
            foreach (var id in cardIds)
            {
                if (_bundle.Cards.TryGetValue(id, out var s))
                    result[id] = s;
                else
                    allHit = false;
            }
            if (allHit) return result;
        }

        // Slow path: fetch missing from API
        var missing = cardIds.Where(id => !result.ContainsKey(id)).ToList();
        if (missing.Count > 0)
        {
            var fetched = await ApiClient.Instance.GetCardStatsBatchAsync(missing, filter);
            if (fetched != null)
            {
                foreach (var kvp in fetched)
                    result[kvp.Key] = kvp.Value;
            }
        }

        // Fill nulls for anything still missing
        foreach (var id in cardIds)
            result.TryAdd(id, null);

        return result;
    }

    // ── Relic Stats ─────────────────────────────────────────

    public RelicStats? GetRelicStats(string relicId)
    {
        // PRD §3.13 — MyDataOnly: bypass community bundle, read local aggregations.
        if (ModConfig.CurrentFilter.MyDataOnly)
        {
            var local = RunHistoryAnalyzer.Instance.LocalRelics.Get(relicId);
            if (local == null || local.RunsWith == 0) return null;
            return new RelicStats
            {
                WinRate = local.WinRate,
                PickRate = 0f,      // Local data doesn't track pick rate
                ShopBuyRate = 0f,   // Local data doesn't track shop buy rate
                SampleSize = local.RunsWith,
            };
        }

        if (_bundle?.Relics.TryGetValue(relicId, out var stats) == true)
            return stats;
        return null;
    }

    // Cached global average relic win rate (recomputed when bundle changes)
    private float _globalAvgRelicWinRate = -1f;
    private int _globalAvgSampleHash;

    /// <summary>
    /// Compute the sample-weighted average win rate across all known relics.
    /// Used for per-relic delta display (PRD 3.4).
    /// Returns 0.5 when no data is available.
    /// </summary>
    public float GetGlobalAverageRelicWinRate()
    {
        if (_bundle == null || _bundle.Relics.Count == 0) return 0.5f;

        // Cheap change detection: use count as a proxy for bundle identity
        var sampleHash = _bundle.Relics.Count;
        if (_globalAvgRelicWinRate >= 0 && _globalAvgSampleHash == sampleHash)
            return _globalAvgRelicWinRate;

        double totalWeight = 0;
        double weightedSum = 0;
        foreach (var stats in _bundle.Relics.Values)
        {
            int n = Math.Max(1, stats.SampleSize);
            totalWeight += n;
            weightedSum += stats.WinRate * n;
        }
        _globalAvgRelicWinRate = totalWeight > 0 ? (float)(weightedSum / totalWeight) : 0.5f;
        _globalAvgSampleHash = sampleHash;
        return _globalAvgRelicWinRate;
    }

    public async Task<Dictionary<string, RelicStats?>> GetRelicStatsBatchAsync(
        List<string> relicIds, FilterSettings filter)
    {
        var result = new Dictionary<string, RelicStats?>();

        if (_bundle != null && FilterMatches(filter))
        {
            var allHit = true;
            foreach (var id in relicIds)
            {
                if (_bundle.Relics.TryGetValue(id, out var s))
                    result[id] = s;
                else
                    allHit = false;
            }
            if (allHit) return result;
        }

        var missing = relicIds.Where(id => !result.ContainsKey(id)).ToList();
        if (missing.Count > 0)
        {
            var fetched = await ApiClient.Instance.GetRelicStatsBatchAsync(missing, filter);
            if (fetched != null)
            {
                foreach (var kvp in fetched)
                    result[kvp.Key] = kvp.Value;
            }
        }

        foreach (var id in relicIds)
            result.TryAdd(id, null);

        return result;
    }

    // ── Event Stats ─────────────────────────────────────────

    public EventStats? GetEventStats(string eventId)
    {
        if (_bundle?.Events.TryGetValue(eventId, out var stats) == true)
            return stats;
        return null;
    }

    public async Task<EventStats?> GetEventStatsAsync(string eventId, FilterSettings filter)
    {
        if (_bundle != null && FilterMatches(filter) &&
            _bundle.Events.TryGetValue(eventId, out var stats))
            return stats;

        return await ApiClient.Instance.GetEventStatsAsync(eventId, filter);
    }

    // ── Encounter Stats ─────────────────────────────────────

    public EncounterStats? GetEncounterStats(string encounterId)
    {
        if (_bundle?.Encounters.TryGetValue(encounterId, out var stats) == true)
            return stats;
        return null;
    }

    public async Task<Dictionary<string, EncounterStats?>> GetEncounterStatsBatchAsync(
        List<string> encounterIds, FilterSettings filter)
    {
        var result = new Dictionary<string, EncounterStats?>();

        if (_bundle != null && FilterMatches(filter))
        {
            var allHit = true;
            foreach (var id in encounterIds)
            {
                if (_bundle.Encounters.TryGetValue(id, out var s))
                    result[id] = s;
                else
                    allHit = false;
            }
            if (allHit) return result;
        }

        var missing = encounterIds.Where(id => !result.ContainsKey(id)).ToList();
        if (missing.Count > 0)
        {
            var fetched = await ApiClient.Instance.GetEncounterStatsBatchAsync(missing, filter);
            if (fetched != null)
            {
                foreach (var kvp in fetched)
                    result[kvp.Key] = kvp.Value;
            }
        }

        foreach (var id in encounterIds)
            result.TryAdd(id, null);

        return result;
    }

    // ── Internal ────────────────────────────────────────────

    private bool FilterMatches(FilterSettings filter) =>
        _bundleFilter != null && filter.Equals(_bundleFilter);

    /// <summary>
    /// Loads the bundled test_data.json shipped next to the mod DLL.
    /// Used as last-resort fallback when both API and disk cache are unavailable.
    /// </summary>
    private static BulkStatsBundle? LoadBundledTestData()
    {
        try
        {
            var asmLocation = typeof(StatsProvider).Assembly.Location;
            if (string.IsNullOrEmpty(asmLocation)) return null;

            var modDir = Path.GetDirectoryName(asmLocation)!;
            var testDataPath = Path.Combine(modDir, "test", "test_data.json");

            if (!File.Exists(testDataPath))
            {
                Safe.Warn($"Test data not found at: {testDataPath}");
                return null;
            }

            var json = File.ReadAllText(testDataPath);
            var bundle = JsonSerializer.Deserialize<BulkStatsBundle>(json);
            return bundle;
        }
        catch (Exception ex)
        {
            Safe.Warn($"Failed to load bundled test data: {ex.Message}");
            return null;
        }
    }
}
