using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityStats.Config;
using CommunityStats.Util;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace CommunityStats.Collection;

/// <summary>
/// Loads RunHistory files in the background and aggregates them into CareerStatsData.
/// Caches per-character results; supports incremental updates when a new run completes.
///
/// Threading model:
///   - LoadAllAsync runs entirely on a worker thread (Task.Run)
///   - Results delivered via the awaited Task; UI subscribes via CareerStatsLoaded event
///   - Reentrancy guarded by SemaphoreSlim
///
/// PRD-04 §3.11. Phase 6 task 1.
/// </summary>
public sealed class RunHistoryAnalyzer
{
    public static RunHistoryAnalyzer Instance { get; } = new();

    // ── Cache ───────────────────────────────────────────────
    // (characterFilter ?? "", minAscension) → cached snapshot
    private readonly Dictionary<(string, int), CareerStatsData> _cache = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    private static (string, int) Key(string? characterFilter, int minAscension)
        => (characterFilter ?? "", minAscension);

    /// <summary>Fired on the worker thread when a new snapshot is ready.</summary>
    public event Action<CareerStatsData>? CareerStatsLoaded;

    // Per-card local aggregations live alongside the career snapshot.
    // Round 9 round 49: set by BuildSnapshot when SaveManager isn't ready
    // yet (or any other early-startup failure). LoadAllAsync reads this and
    // refuses to cache the resulting empty snapshot.
    private bool _lastBuildFailed;

    private LocalCardStatsBundle _cardBundle = LocalCardStatsBundle.Empty;
    public LocalCardStatsBundle LocalCards => _cardBundle;

    // Per-relic local aggregations (PRD §3.3 round 6).
    private LocalRelicStatsBundle _relicBundle = LocalRelicStatsBundle.Empty;
    public LocalRelicStatsBundle LocalRelics => _relicBundle;

    private RunHistoryAnalyzer() { }

    // ── Public API ──────────────────────────────────────────

    /// <summary>
    /// Returns the cached snapshot if present in memory or on disk; otherwise null.
    /// Use to populate UI synchronously without re-loading. Disk hits are
    /// promoted into the in-memory cache so subsequent calls are free.
    /// </summary>
    public CareerStatsData? GetCached(string? characterFilter, int minAscension = 0)
    {
        var key = Key(characterFilter, minAscension);
        lock (_cache)
        {
            if (_cache.TryGetValue(key, out var v)) return v;
        }

        // Disk fallback only for the unfiltered (asc=0) snapshot — we don't
        // persist per-ascension snapshots.
        //
        // Round 9 round 49: do NOT seed the in-memory `_cache` from disk here.
        // The disk cache only stores CareerStatsData, not the per-card /
        // per-relic bundles. If we wrote it back into `_cache`, the next
        // LoadAllAsync would short-circuit on the cache hit and never run
        // BuildSnapshot — leaving LocalCards / LocalRelics empty until the
        // player finished a run (which forces invalidation). The card library
        // and relic collection would render zeros across the entire session.
        if (minAscension == 0)
        {
            return CareerStatsCache.Load(characterFilter);
        }
        return null;
    }

    /// <summary>
    /// Asynchronously load all RunHistory files (filtered by character if non-null)
    /// and aggregate into CareerStatsData. Result is cached and event-fired.
    /// Pass force=true to bypass the cache (used after a run finishes so the
    /// fresh on-disk file gets re-aggregated).
    /// </summary>
    public async Task<CareerStatsData> LoadAllAsync(string? characterFilter, CancellationToken ct = default, bool force = false, int minAscension = 0)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var key = Key(characterFilter, minAscension);
            // Re-check cache after lock acquisition (skip if forcing).
            if (!force)
            {
                lock (_cache)
                {
                    if (_cache.TryGetValue(key, out var cached)) return cached;
                }
            }
            else
            {
                lock (_cache) _cache.Remove(key);
            }

            Safe.Info($"[RunHistoryAnalyzer] LoadAllAsync starting (filter={characterFilter ?? "all"}, minAsc={minAscension}, force={force})");
            var snapshot = await Task.Run(() => BuildSnapshot(characterFilter, minAscension, ct), ct)
                                     .ConfigureAwait(false);
            Safe.Info($"[RunHistoryAnalyzer] LoadAllAsync built snapshot: TotalRuns={snapshot.TotalRuns}, cards={_cardBundle.Cards.Count}, relics={_relicBundle.Relics.Count}");

            // Round 9 round 49: do NOT cache or persist snapshots that came
            // from a failed BuildSnapshot (signaled by `_lastBuildFailed`).
            // Otherwise we poison the in-memory cache with TotalRuns=0 from a
            // pre-profile-init startup load, and every subsequent LoadAllAsync
            // hits that empty entry until a run end forces invalidation —
            // which is exactly the bug the user reported.
            if (_lastBuildFailed)
            {
                _lastBuildFailed = false;
                Safe.Warn("[RunHistoryAnalyzer] BuildSnapshot reported failure — not caching empty snapshot");
                return snapshot;
            }

            lock (_cache) { _cache[key] = snapshot; }

            // Persist only the asc=0 snapshot (per-ascension snapshots aren't
            // worth caching to disk — they're cheap to recompute).
            if (minAscension == 0)
            {
                try { CareerStatsCache.Save(snapshot); }
                catch (Exception ex) { Safe.Warn($"CareerStatsCache.Save threw: {ex.Message}"); }
            }

            try { CareerStatsLoaded?.Invoke(snapshot); }
            catch (Exception ex) { Safe.Warn($"CareerStatsLoaded handler threw: {ex.Message}"); }

            return snapshot;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Invalidate all cached snapshots. Call when a run finishes so the next read reloads.
    /// Removes both in-memory and on-disk caches AND the per-card / per-relic
    /// bundles — without resetting the bundles, the lazy reload in
    /// CardLibraryPatch / RelicLibraryPatch (which checks `TotalRuns == 0`)
    /// would never re-trigger after a run end.
    /// </summary>
    public void InvalidateAll()
    {
        lock (_cache) _cache.Clear();
        CareerStatsCache.DeleteAll();
        _cardBundle = LocalCardStatsBundle.Empty;
        _relicBundle = LocalRelicStatsBundle.Empty;
        Safe.Info("[RunHistoryAnalyzer] InvalidateAll: cache + bundles cleared");
    }

    // ── Worker ──────────────────────────────────────────────

    private CareerStatsData BuildSnapshot(string? characterFilter, int minAscension, CancellationToken ct)
    {
        _lastBuildFailed = false;
        List<string> names;
        try
        {
            names = SaveManager.Instance?.GetAllRunHistoryNames() ?? new List<string>();
        }
        catch (Exception ex)
        {
            Safe.Warn($"RunHistoryAnalyzer: GetAllRunHistoryNames failed: {ex.Message}");
            _lastBuildFailed = true;
            return CareerStatsData.Empty(characterFilter, minAscension);
        }

        Safe.Info($"[RunHistoryAnalyzer] BuildSnapshot: GetAllRunHistoryNames returned {names.Count} files");
        if (names.Count == 0) return CareerStatsData.Empty(characterFilter, minAscension);

        // Load all matching histories. Each load is wrapped to survive single-file failure.
        var loaded = new List<RunHistory>(capacity: names.Count);
        foreach (var name in names)
        {
            ct.ThrowIfCancellationRequested();
            RunHistory? history;
            try
            {
                var result = SaveManager.Instance.LoadRunHistory(name);
                if (result == null || !result.Success || result.SaveData == null) continue;
                history = result.SaveData;
            }
            catch (Exception ex)
            {
                Safe.Warn($"RunHistoryAnalyzer: failed to load '{name}': {ex.Message}");
                continue;
            }

            if (characterFilter != null)
            {
                bool matches = history.Players.Any(p => p.Character.Entry == characterFilter);
                if (!matches) continue;
            }
            // Round 9 round 46: ascension floor filter (>= minAscension).
            if (minAscension > 0 && history.Ascension < minAscension) continue;
            loaded.Add(history);
        }

        Safe.Info($"[RunHistoryAnalyzer] BuildSnapshot: loaded {loaded.Count} histories (filter={characterFilter ?? "all"}, minAsc={minAscension})");
        if (loaded.Count == 0)
        {
            // Round 9 round 36: bundles must reset to Empty when no histories
            // match, otherwise stale data leaks across filter changes.
            _cardBundle = LocalCardStatsBundle.Empty;
            _relicBundle = LocalRelicStatsBundle.Empty;
            return CareerStatsData.Empty(characterFilter, minAscension);
        }

        // Sort newest first by StartTime for rolling-window calculation.
        loaded.Sort((a, b) => b.StartTime.CompareTo(a.StartTime));

        // Per-card / per-relic local aggregations run in the same loop.
        // Results are cached on the analyzer instance so the card library
        // and relic library compendia can read them synchronously without
        // re-walking files.
        _cardBundle = ComputeLocalCardBundle(loaded);
        _relicBundle = ComputeLocalRelicBundle(loaded);

        return new CareerStatsData
        {
            CharacterFilter = characterFilter,
            MinAscension = minAscension,
            TotalRuns = loaded.Count,
            Wins = loaded.Count(r => r.Win),
            MaxWinStreak = ComputeMaxWinStreak(loaded),
            CurrentWinStreak = ComputeCurrentWinStreak(loaded),
            WinRateByWindow = ComputeWinRateWindows(loaded),
            DeathCausesByAct = ComputeDeathCauses(loaded),
            PathStatsByAct = ComputePathStats(loaded),
            AncientPickRates = ComputeAncientPickRates(loaded),
            BossStats = ComputeBossStats(loaded),
            AncientByElder = ComputeAncientByElder(loaded),
        };
    }

    // ── Aggregations ────────────────────────────────────────

    /// <summary>
    /// When KilledBy* fields are both unset, look at the deepest visited floor.
    /// If it was a combat room (Monster/Elite/Boss), return its encounter id so
    /// we attribute the abandon to the fight the player was actually inside.
    /// Returns null when no combat floor was found.
    /// </summary>
    private static string? ResolveAbandonedCause(RunHistory run)
    {
        if (run.MapPointHistory == null) return null;
        for (int actIdx = run.MapPointHistory.Count - 1; actIdx >= 0; actIdx--)
        {
            var floors = run.MapPointHistory[actIdx];
            if (floors == null) continue;
            for (int f = floors.Count - 1; f >= 0; f--)
            {
                var floor = floors[f];
                if (floor == null) continue;
                bool isCombat = floor.MapPointType == MapPointType.Monster
                             || floor.MapPointType == MapPointType.Elite
                             || floor.MapPointType == MapPointType.Boss;
                if (!isCombat) continue;
                if (floor.Rooms == null || floor.Rooms.Count == 0) continue;
                var entry = floor.Rooms[0].ModelId.Entry;
                if (!string.IsNullOrEmpty(entry)) return entry;
            }
        }
        return null;
    }


    /// <summary>
    /// Round 9 round 49: longest consecutive-win streak among the filtered
    /// runs. Walks chronologically (oldest → newest), counting consecutive
    /// Win=true and resetting on a loss.
    /// </summary>
    private static int ComputeMaxWinStreak(List<RunHistory> sortedNewestFirst)
    {
        int best = 0, cur = 0;
        // Walk in chronological order: reverse iterate the newest-first list.
        for (int i = sortedNewestFirst.Count - 1; i >= 0; i--)
        {
            if (sortedNewestFirst[i].Win)
            {
                cur++;
                if (cur > best) best = cur;
            }
            else
            {
                cur = 0;
            }
        }
        return best;
    }

    /// <summary>
    /// Round 9 round 52: count consecutive wins starting from the newest run
    /// backwards. Returns 0 as soon as a loss is encountered. Under filter
    /// constraints this is "your current active streak".
    /// </summary>
    private static int ComputeCurrentWinStreak(List<RunHistory> sortedNewestFirst)
    {
        int cur = 0;
        foreach (var run in sortedNewestFirst)
        {
            if (run.Win) cur++;
            else break;
        }
        return cur;
    }

    private static IReadOnlyDictionary<int, float> ComputeWinRateWindows(List<RunHistory> sortedNewestFirst)
    {
        var result = new Dictionary<int, float>();
        foreach (var window in new[] { 10, 50, 100, int.MaxValue })
        {
            var slice = sortedNewestFirst.Take(window).ToList();
            if (slice.Count == 0) { result[window] = 0f; continue; }
            int wins = slice.Count(r => r.Win);
            result[window] = (float)wins / slice.Count;
        }
        return result;
    }

    private static IReadOnlyDictionary<int, IReadOnlyList<DeathEntry>> ComputeDeathCauses(List<RunHistory> runs)
    {
        // Per-Act bucket: actIndex → (encounterId, source) → count
        var bucket = new Dictionary<int, Dictionary<(string, DeathSource), int>>();

        foreach (var run in runs)
        {
            if (run.Win) continue;

            int actIndex = Math.Max(1, run.MapPointHistory?.Count ?? 1);
            string cause;
            DeathSource src;
            if (run.KilledByEncounter != ModelId.none)
            {
                cause = run.KilledByEncounter.Entry;
                src = DeathSource.Combat;
            }
            else if (run.KilledByEvent != ModelId.none)
            {
                cause = run.KilledByEvent.Entry;
                src = DeathSource.Event;
            }
            else
            {
                // Walk back to the last visited combat floor; that's a combat
                // death attributed to the encounter we found there.
                var fallback = ResolveAbandonedCause(run);
                if (fallback != null)
                {
                    cause = fallback;
                    src = DeathSource.Combat;
                }
                else
                {
                    cause = "ABANDONED";
                    src = DeathSource.Abandoned;
                }
            }

            if (!bucket.TryGetValue(actIndex, out var map))
            {
                map = new Dictionary<(string, DeathSource), int>();
                bucket[actIndex] = map;
            }
            var key = (cause, src);
            map[key] = map.GetValueOrDefault(key) + 1;
        }

        var result = new Dictionary<int, IReadOnlyList<DeathEntry>>();
        foreach (var (act, map) in bucket)
        {
            int total = map.Values.Sum();
            var rows = map
                .OrderByDescending(kv => kv.Value)
                .Take(5)
                .Select(kv => new DeathEntry
                {
                    EncounterId = kv.Key.Item1,
                    Source = kv.Key.Item2,
                    Count = kv.Value,
                    Share = total > 0 ? (float)kv.Value / total : 0f,
                })
                .ToList();
            result[act] = rows;
        }
        return result;
    }

    private static IReadOnlyDictionary<int, ActPathStats> ComputePathStats(List<RunHistory> runs)
    {
        // Accumulators per Act index (1..N).
        var totals = new Dictionary<int, (long Gained, long Bought, long Removed, long Upgraded,
                                          long Unknown, long Monster, long Elite, long Shop, long Campfire, int Samples)>();

        foreach (var run in runs)
        {
            if (run.MapPointHistory == null) continue;
            for (int i = 0; i < run.MapPointHistory.Count; i++)
            {
                int actIdx = i + 1; // 1-based
                var floors = run.MapPointHistory[i];
                if (floors == null || floors.Count == 0) continue;

                long gained = 0, bought = 0, removed = 0, upgraded = 0;
                long unknown = 0, monster = 0, elite = 0, shop = 0, campfire = 0;

                foreach (var floor in floors)
                {
                    switch (floor.MapPointType)
                    {
                        case MapPointType.Unknown:  unknown++;  break;
                        case MapPointType.Monster:  monster++;  break;
                        case MapPointType.Elite:    elite++;    break;
                        case MapPointType.Shop:     shop++;     break;
                        case MapPointType.RestSite: campfire++; break;
                    }

                    if (floor.PlayerStats == null) continue;
                    foreach (var ps in floor.PlayerStats)
                    {
                        gained   += ps.CardsGained?.Count ?? 0;
                        removed  += ps.CardsRemoved?.Count ?? 0;
                        upgraded += ps.UpgradedCards?.Count ?? 0;
                        // BoughtRelics list contains relics; bought cards are tracked
                        // separately (via BoughtColorless + shop card purchases routed
                        // through CardsGained on the shop floor). Use BoughtColorless
                        // as a proxy for cards bought from shop until we have a richer
                        // signal.
                        bought   += ps.BoughtColorless?.Count ?? 0;
                    }
                }

                var prev = totals.GetValueOrDefault(actIdx);
                totals[actIdx] = (
                    prev.Gained + gained,
                    prev.Bought + bought,
                    prev.Removed + removed,
                    prev.Upgraded + upgraded,
                    prev.Unknown + unknown,
                    prev.Monster + monster,
                    prev.Elite + elite,
                    prev.Shop + shop,
                    prev.Campfire + campfire,
                    prev.Samples + 1);
            }
        }

        var result = new Dictionary<int, ActPathStats>();
        foreach (var (act, t) in totals)
        {
            float n = Math.Max(1, t.Samples);
            result[act] = new ActPathStats
            {
                CardsGained   = t.Gained / n,
                CardsBought   = t.Bought / n,
                CardsRemoved  = t.Removed / n,
                CardsUpgraded = t.Upgraded / n,
                UnknownRooms  = t.Unknown / n,
                MonsterRooms  = t.Monster / n,
                EliteRooms    = t.Elite / n,
                ShopRooms     = t.Shop / n,
                CampfireRooms = t.Campfire / n,
                SampleSize    = t.Samples,
            };
        }
        return result;
    }

    private static IReadOnlyDictionary<string, AncientChoiceStats> ComputeAncientPickRates(List<RunHistory> runs)
    {
        var bucket = new Dictionary<string, (int Opportunities, int Picks)>();

        foreach (var run in runs)
        {
            if (run.MapPointHistory == null) continue;
            foreach (var floors in run.MapPointHistory)
            {
                foreach (var floor in floors)
                {
                    if (floor.PlayerStats == null) continue;
                    foreach (var ps in floor.PlayerStats)
                    {
                        if (ps.AncientChoices == null || ps.AncientChoices.Count == 0) continue;
                        // Each AncientChoices entry is one option offered in this ancient encounter.
                        foreach (var choice in ps.AncientChoices)
                        {
                            string key;
                            try { key = choice.TextKey; }
                            catch { continue; }
                            if (string.IsNullOrEmpty(key)) continue;

                            var prev = bucket.GetValueOrDefault(key);
                            bucket[key] = (prev.Opportunities + 1, prev.Picks + (choice.WasChosen ? 1 : 0));
                        }
                    }
                }
            }
        }

        var result = new Dictionary<string, AncientChoiceStats>();
        foreach (var (key, t) in bucket)
        {
            result[key] = new AncientChoiceStats
            {
                TextKey = key,
                Opportunities = t.Opportunities,
                Picks = t.Picks,
            };
        }
        return result;
    }

    private static IReadOnlyDictionary<string, BossEncounterStats> ComputeBossStats(List<RunHistory> runs)
    {
        // bossId → (totalDmg, encounters, deaths)
        var bucket = new Dictionary<string, (long Damage, int Encounters, int Deaths)>();

        foreach (var run in runs)
        {
            if (run.MapPointHistory == null) continue;
            // Last floor (where the player died if losing) is the deepest entry.
            string? deathRoomEncounterId = null;
            if (!run.Win)
            {
                if (run.KilledByEncounter != ModelId.none) deathRoomEncounterId = run.KilledByEncounter.Entry;
            }

            foreach (var floors in run.MapPointHistory)
            {
                foreach (var floor in floors)
                {
                    if (floor.MapPointType != MapPointType.Boss) continue;
                    if (floor.Rooms == null || floor.Rooms.Count == 0) continue;

                    // Use the first room's model id as boss id.
                    string bossId = floor.Rooms[0].ModelId.Entry;
                    if (string.IsNullOrEmpty(bossId)) bossId = "UNKNOWN_BOSS";

                    int dmg = 0;
                    if (floor.PlayerStats != null)
                    {
                        foreach (var ps in floor.PlayerStats) dmg += ps.DamageTaken;
                    }
                    bool died = bossId == deathRoomEncounterId;

                    var prev = bucket.GetValueOrDefault(bossId);
                    bucket[bossId] = (prev.Damage + dmg, prev.Encounters + 1, prev.Deaths + (died ? 1 : 0));
                }
            }
        }

        var result = new Dictionary<string, BossEncounterStats>();
        foreach (var (id, t) in bucket)
        {
            result[id] = new BossEncounterStats
            {
                EncounterId = id,
                Encounters = t.Encounters,
                Deaths = t.Deaths,
                AverageDamageTaken = t.Encounters > 0 ? (float)t.Damage / t.Encounters : 0f,
            };
        }
        return result;
    }

    /// <summary>
    /// Aggregate per-Elder breakdown for the PRD §3.11 dropdown.
    /// Walks every Ancient floor in the run history and groups offered relics
    /// under their parent elder encounter id.
    /// </summary>
    private static IReadOnlyDictionary<string, ElderEntry> ComputeAncientByElder(List<RunHistory> runs)
    {
        // elderId → optionRelicId → (picks, wins)
        var bucket = new Dictionary<string, Dictionary<string, (int picks, int wins)>>();
        var elderEncounters = new Dictionary<string, int>();

        foreach (var run in runs)
        {
            if (run.MapPointHistory == null) continue;
            foreach (var floors in run.MapPointHistory)
            {
                if (floors == null) continue;
                foreach (var floor in floors)
                {
                    if (floor.MapPointType != MapPointType.Ancient) continue;
                    if (floor.Rooms == null || floor.Rooms.Count == 0) continue;

                    string elderId = floor.Rooms[0].ModelId.Entry;
                    if (string.IsNullOrEmpty(elderId)) continue;

                    elderEncounters[elderId] = elderEncounters.GetValueOrDefault(elderId) + 1;

                    if (floor.PlayerStats == null) continue;
                    foreach (var ps in floor.PlayerStats)
                    {
                        if (ps.AncientChoices == null) continue;
                        foreach (var choice in ps.AncientChoices)
                        {
                            string relicId = ExtractRelicId(choice);
                            if (string.IsNullOrEmpty(relicId)) continue;

                            if (!bucket.TryGetValue(elderId, out var inner))
                            {
                                inner = new Dictionary<string, (int, int)>();
                                bucket[elderId] = inner;
                            }
                            var prev = inner.GetValueOrDefault(relicId);
                            int picks = prev.picks + (choice.WasChosen ? 1 : 0);
                            int wins = prev.wins + (choice.WasChosen && run.Win ? 1 : 0);
                            inner[relicId] = (picks, wins);
                        }
                    }
                }
            }
        }

        var result = new Dictionary<string, ElderEntry>();
        foreach (var (elderId, inner) in bucket)
        {
            var relics = inner
                .Select(kv => new ElderRelicStats
                {
                    RelicId = kv.Key,
                    Picks = kv.Value.picks,
                    Wins = kv.Value.wins,
                })
                .OrderByDescending(r => r.Picks)
                .ToList();

            // Each Pael / Darv / etc. event normally offers ONE option set; we
            // group all observed relics under a single "all" option to keep
            // the data shape compatible with the PRD dropdown.
            var option = new ElderOption
            {
                OptionTextKey = "all",
                Picks = relics.Sum(r => r.Picks),
                Relics = relics,
            };

            result[elderId] = new ElderEntry
            {
                ElderId = elderId,
                Encounters = elderEncounters.GetValueOrDefault(elderId),
                Options = new List<ElderOption> { option },
            };
        }
        return result;
    }

    /// <summary>
    /// Pull a relic id out of an AncientChoiceHistoryEntry. The Title.LocEntryKey
    /// is "RELIC_ID.title" when the option came from EventOption.FromRelic, which
    /// is the path Pael/Darv/Tezcatara/etc. all use.
    /// </summary>
    private static string ExtractRelicId(MegaCrit.Sts2.Core.Runs.History.AncientChoiceHistoryEntry choice)
    {
        try
        {
            var key = choice.Title?.LocEntryKey;
            if (string.IsNullOrEmpty(key)) return "";
            // "RELIC_ID.title" → "RELIC_ID". Anything else: return as-is.
            int dot = key!.LastIndexOf('.');
            return dot > 0 ? key.Substring(0, dot) : key;
        }
        catch { return ""; }
    }

    /// <summary>
    /// Walk every loaded RunHistory and compute per-card sample / win rates.
    /// Round 9 round 38: a card's RunsWith is the **union** of two signals:
    ///   1. `PlayerMapPointHistoryEntry.CardsGained` — every card added to
    ///      PileType.Deck mid-run (rewards, events, shops, transforms).
    ///   2. `RunHistoryPlayer.Deck` — the final deck snapshot at run end.
    ///
    /// Why both? `CardsGained` is **only written when adding to Deck**, so
    /// starter cards (Strike/Defend ×N seeded into the player's initial deck
    /// before any MapPointHistoryEntry exists) are never recorded there.
    /// Without the Deck-snapshot fallback, every player would see 0 samples
    /// for basic strikes/defends. Conversely `Deck` alone misses cards that
    /// were picked then removed/transformed — using both gives us "any card
    /// the player ever had in this run" which matches user intent.
    ///
    /// Counts:
    ///   Offered  — # CardChoiceHistoryEntry rows mentioning the card
    ///   Picks    — subset of Offered where wasPicked == true
    ///   RunsWith — # distinct runs that contained the card via either path
    ///   WinsAfter — # of those runs where run.Win == true
    /// </summary>
    private static LocalCardStatsBundle ComputeLocalCardBundle(List<RunHistory> runs)
    {
        // cardId → (offered, picks, runsWith, winsAfter, upgraded, removed, bought)
        var bucket = new Dictionary<string,
            (int offered, int picks, int runsWith, int winsAfter,
             int upgraded, int removed, int bought)>();

        int diagRunsWithGained = 0;
        int diagRunsWithDeck = 0;
        int diagRunsWithUpgraded = 0;
        int diagRunsWithRemoved = 0;
        int diagRunsWithBought = 0;

        foreach (var run in runs)
        {
            var runCards = new HashSet<string>();
            // Per-run sets so each card is counted at most once for each rate.
            var runUpgraded = new HashSet<string>();
            var runRemoved  = new HashSet<string>();
            var runBought   = new HashSet<string>();

            // Source 1: walk MapPointHistory floor-by-floor for choices,
            // gains, upgrades, removals, shop purchases.
            if (run.MapPointHistory != null)
            {
                foreach (var floors in run.MapPointHistory)
                {
                    if (floors == null) continue;
                    foreach (var floor in floors)
                    {
                        if (floor.PlayerStats == null) continue;
                        bool isShopFloor = floor.MapPointType
                            == MegaCrit.Sts2.Core.Map.MapPointType.Shop;

                        foreach (var ps in floor.PlayerStats)
                        {
                            // Pick / offer counts.
                            if (ps.CardChoices != null)
                            {
                                foreach (var choice in ps.CardChoices)
                                {
                                    var id = choice.Card.Id?.Entry;
                                    if (string.IsNullOrEmpty(id)) continue;
                                    var prev = bucket.GetValueOrDefault(id!);
                                    bucket[id!] = (prev.offered + 1,
                                                   prev.picks + (choice.wasPicked ? 1 : 0),
                                                   prev.runsWith,
                                                   prev.winsAfter,
                                                   prev.upgraded,
                                                   prev.removed,
                                                   prev.bought);
                                }
                            }

                            // Cards gained from any source.
                            if (ps.CardsGained != null)
                            {
                                foreach (var card in ps.CardsGained)
                                {
                                    var id = card.Id?.Entry;
                                    if (string.IsNullOrEmpty(id)) continue;
                                    runCards.Add(id!);
                                    // Cards gained on a Shop floor count as bought.
                                    if (isShopFloor) runBought.Add(id!);
                                }
                            }

                            // Upgrades performed this floor.
                            if (ps.UpgradedCards != null)
                            {
                                foreach (var mid in ps.UpgradedCards)
                                {
                                    var id = mid.Entry;
                                    if (!string.IsNullOrEmpty(id)) runUpgraded.Add(id);
                                }
                            }

                            // Removals performed this floor.
                            if (ps.CardsRemoved != null)
                            {
                                foreach (var card in ps.CardsRemoved)
                                {
                                    var id = card?.Id?.Entry;
                                    if (!string.IsNullOrEmpty(id)) runRemoved.Add(id!);
                                }
                            }

                            // Colorless cards bought from merchant.
                            if (ps.BoughtColorless != null)
                            {
                                foreach (var mid in ps.BoughtColorless)
                                {
                                    var id = mid.Entry;
                                    if (!string.IsNullOrEmpty(id)) runBought.Add(id);
                                }
                            }
                        }
                    }
                }
            }
            int beforeDeck = runCards.Count;
            if (beforeDeck > 0) diagRunsWithGained++;

            // Source 2: final Deck snapshot for starter cards.
            try
            {
                if (run.Players != null)
                {
                    foreach (var player in run.Players)
                    {
                        if (player.Deck == null) continue;
                        foreach (var card in player.Deck)
                        {
                            var id = card?.Id?.Entry;
                            if (!string.IsNullOrEmpty(id)) runCards.Add(id!);
                        }
                    }
                }
            }
            catch { /* malformed save — skip silently */ }
            if (runCards.Count > beforeDeck) diagRunsWithDeck++;
            if (runUpgraded.Count > 0) diagRunsWithUpgraded++;
            if (runRemoved.Count > 0)  diagRunsWithRemoved++;
            if (runBought.Count > 0)   diagRunsWithBought++;

            foreach (var id in runCards)
            {
                var prev = bucket.GetValueOrDefault(id);
                bucket[id] = (prev.offered, prev.picks,
                              prev.runsWith + 1,
                              prev.winsAfter + (run.Win ? 1 : 0),
                              prev.upgraded + (runUpgraded.Contains(id) ? 1 : 0),
                              prev.removed  + (runRemoved.Contains(id)  ? 1 : 0),
                              prev.bought   + (runBought.Contains(id)   ? 1 : 0));
            }
        }

        Safe.Info($"[RunHistoryAnalyzer] Card bundle: {bucket.Count} distinct cards, " +
                  $"gained={diagRunsWithGained}/{runs.Count}, " +
                  $"deck={diagRunsWithDeck}/{runs.Count}, " +
                  $"upgraded={diagRunsWithUpgraded}, removed={diagRunsWithRemoved}, bought={diagRunsWithBought}");

        var dict = new Dictionary<string, LocalCardStats>(bucket.Count);
        foreach (var (id, t) in bucket)
        {
            dict[id] = new LocalCardStats
            {
                CardId = id,
                Offered = t.offered,
                Picks = t.picks,
                RunsWith = t.runsWith,
                WinsAfter = t.winsAfter,
                RunsUpgraded = t.upgraded,
                RunsRemoved = t.removed,
                RunsBought = t.bought,
            };
        }
        return new LocalCardStatsBundle
        {
            TotalRuns = runs.Count,
            Cards = dict,
        };
    }

    /// <summary>
    /// Walk every loaded RunHistory and compute per-relic win rates.
    /// PRD §3.3 round 6: feeds the "我的数据" column of the relic library.
    ///
    /// "Owned a relic" is detected from `RunHistoryPlayer.Relics` (the final
    /// inventory snapshot). For older saves that don't have that field we
    /// fall back to walking `BoughtRelics` + `RelicChoices.WasChosen`.
    /// </summary>
    private static LocalRelicStatsBundle ComputeLocalRelicBundle(List<RunHistory> runs)
    {
        var bucket = new Dictionary<string, (int runs, int wins)>();

        foreach (var run in runs)
        {
            // Per-run set of relics seen so we count each relic once per run.
            var relics = new HashSet<string>();

            try
            {
                foreach (var player in run.Players)
                {
                    if (player.Relics == null) continue;
                    foreach (var sr in player.Relics)
                    {
                        var entry = sr?.Id?.Entry;
                        if (!string.IsNullOrEmpty(entry)) relics.Add(entry!);
                    }
                }
            }
            catch { /* fall through to MapPointHistory walk */ }

            // Fallback: walk floor history if Players[].Relics is empty.
            if (relics.Count == 0 && run.MapPointHistory != null)
            {
                foreach (var floors in run.MapPointHistory)
                {
                    if (floors == null) continue;
                    foreach (var floor in floors)
                    {
                        if (floor.PlayerStats == null) continue;
                        foreach (var ps in floor.PlayerStats)
                        {
                            if (ps.BoughtRelics != null)
                                foreach (var rid in ps.BoughtRelics)
                                    if (!string.IsNullOrEmpty(rid.Entry)) relics.Add(rid.Entry);
                            if (ps.RelicChoices != null)
                                foreach (var choice in ps.RelicChoices)
                                {
                                    if (!choice.wasPicked) continue;
                                    var entry = choice.choice?.Entry;
                                    if (!string.IsNullOrEmpty(entry)) relics.Add(entry!);
                                }
                        }
                    }
                }
            }

            foreach (var id in relics)
            {
                var prev = bucket.GetValueOrDefault(id);
                bucket[id] = (prev.runs + 1, prev.wins + (run.Win ? 1 : 0));
            }
        }

        var dict = new Dictionary<string, LocalRelicStats>(bucket.Count);
        float sumWinRate = 0f;
        int countWithData = 0;
        foreach (var (id, t) in bucket)
        {
            var entry = new LocalRelicStats
            {
                RelicId = id,
                RunsWith = t.runs,
                WinsWith = t.wins,
            };
            dict[id] = entry;
            if (entry.RunsWith > 0)
            {
                sumWinRate += entry.WinRate;
                countWithData++;
            }
        }
        return new LocalRelicStatsBundle
        {
            TotalRuns = runs.Count,
            Relics = dict,
            AverageWinRate = countWithData > 0 ? sumWinRate / countWithData : 0f,
        };
    }

    // ── Single-run analysis ─────────────────────────────────

    /// <summary>
    /// Build per-Act stats for a SINGLE run (used by Run History detail view, PRD §3.12).
    /// Synchronous — input data is already loaded.
    /// </summary>
    public SingleRunStatsData BuildSingleRunStats(RunHistory run)
    {
        var path = new Dictionary<int, ActPathStats>();
        var ancients = new List<string>();
        var bossDmg = new Dictionary<string, int>();

        // PlayerMapPointHistoryEntry.BoughtColorless only records colorless-pool
        // card buys. Colored shop buys (Ironclad buys Strike+ at a shop, etc.)
        // never land in that list, so CardsBought was always undercounted. We
        // record every shop card purchase via ShopPatch.BeforeCardPurchaseVisual
        // → RunDataCollector.RecordShopPurchase → ShopPurchasePersistence; here
        // we load by seed and bucket by act. Falls back to BoughtColorless for
        // runs imported before this fix (no persistence file).
        var perActBuys = BuildPerActShopCardBuys(run);

        if (run.MapPointHistory != null)
        {
            for (int i = 0; i < run.MapPointHistory.Count; i++)
            {
                int actIdx = i + 1;
                var floors = run.MapPointHistory[i];
                if (floors == null) continue;

                int gained = 0, bought = 0, removed = 0, upgraded = 0;
                int unknown = 0, monster = 0, elite = 0, shop = 0, campfire = 0;
                bool usePersisted = perActBuys != null;

                foreach (var floor in floors)
                {
                    switch (floor.MapPointType)
                    {
                        case MapPointType.Unknown:  unknown++;  break;
                        case MapPointType.Monster:  monster++;  break;
                        case MapPointType.Elite:    elite++;    break;
                        case MapPointType.Shop:     shop++;     break;
                        case MapPointType.RestSite: campfire++; break;
                    }

                    if (floor.PlayerStats == null) continue;
                    foreach (var ps in floor.PlayerStats)
                    {
                        gained   += ps.CardsGained?.Count ?? 0;
                        removed  += ps.CardsRemoved?.Count ?? 0;
                        upgraded += ps.UpgradedCards?.Count ?? 0;
                        if (!usePersisted)
                            bought += ps.BoughtColorless?.Count ?? 0;

                        if (ps.AncientChoices != null)
                        {
                            foreach (var c in ps.AncientChoices)
                            {
                                if (!c.WasChosen) continue;
                                try { ancients.Add(c.TextKey); } catch { }
                            }
                        }
                    }

                    if (floor.MapPointType == MapPointType.Boss && floor.Rooms?.Count > 0)
                    {
                        string bossId = floor.Rooms[0].ModelId.Entry;
                        if (!string.IsNullOrEmpty(bossId) && floor.PlayerStats != null)
                        {
                            int dmg = 0;
                            foreach (var ps in floor.PlayerStats) dmg += ps.DamageTaken;
                            bossDmg[bossId] = dmg;
                        }
                    }
                }

                if (usePersisted)
                    bought = perActBuys!.GetValueOrDefault(actIdx, 0);

                path[actIdx] = new ActPathStats
                {
                    CardsGained = gained,
                    CardsBought = bought,
                    CardsRemoved = removed,
                    CardsUpgraded = upgraded,
                    UnknownRooms = unknown,
                    MonsterRooms = monster,
                    EliteRooms = elite,
                    ShopRooms = shop,
                    CampfireRooms = campfire,
                    SampleSize = 1,
                };
            }
        }

        return new SingleRunStatsData
        {
            Seed = run.Seed,
            Character = run.Players.FirstOrDefault()?.Character.Entry ?? "",
            Win = run.Win,
            Ascension = run.Ascension,
            FloorReached = run.MapPointHistory?.Sum(act => act?.Count ?? 0) ?? 0,
            PathStatsByAct = path,
            AncientChoicesPicked = ancients,
            BossDamageTaken = bossDmg,
        };
    }

    /// <summary>
    /// Load <see cref="ShopPurchasePersistence"/> for this run's seed and
    /// bucket card purchases by act. Stored <c>Floor</c> is the game's running
    /// <c>RunState.TotalFloor</c> at purchase time, so act N contains
    /// purchases with <c>floor ∈ (startFloor, startFloor + |MapPointHistory[N-1]|]</c>.
    /// Returns null when no persistence file exists (historical / pre-fix runs);
    /// caller falls back to <c>BoughtColorless</c>.
    /// </summary>
    private static Dictionary<int, int>? BuildPerActShopCardBuys(RunHistory run)
    {
        if (string.IsNullOrEmpty(run.Seed) || run.MapPointHistory == null) return null;
        var purchases = ShopPurchasePersistence.Load(run.Seed);
        if (purchases == null) return null;

        var result = new Dictionary<int, int>();
        int startFloor = 0;
        for (int i = 0; i < run.MapPointHistory.Count; i++)
        {
            int actIdx = i + 1;
            int actFloorCount = run.MapPointHistory[i]?.Count ?? 0;
            int endFloor = startFloor + actFloorCount;

            int count = 0;
            foreach (var p in purchases)
            {
                if (p.ItemType != "card") continue;
                if (p.Floor > startFloor && p.Floor <= endFloor) count++;
            }
            result[actIdx] = count;

            startFloor = endFloor;
        }
        return result;
    }
}
