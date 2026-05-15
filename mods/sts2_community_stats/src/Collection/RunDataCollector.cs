using System.Linq;
using CommunityStats.Api;
using CommunityStats.Config;
using CommunityStats.Util;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace CommunityStats.Collection;

/// <summary>
/// Hooked into ModManager.OnMetricsUpload. Assembles the complete RunUploadPayload
/// from SerializableRun + locally tracked data (card choices, event choices, shop,
/// combat contributions) and submits via ApiClient.
/// </summary>
public static class RunDataCollector
{
    // ── Tracked data (populated by Harmony patches during the run) ──

    private static readonly List<CardChoiceUpload> _cardChoices = new();
    private static readonly List<EventChoiceUpload> _eventChoices = new();
    private static readonly List<ShopPurchaseUpload> _shopPurchases = new();
    private static readonly List<ShopCardOfferingUpload> _shopCardOfferings = new();
    private static readonly List<CardRemovalUpload> _cardRemovals = new();
    private static readonly List<CardUpgradeUpload> _cardUpgrades = new();

    /// <summary>
    /// Live floor count derived from the run state's MapPointHistory. The
    /// game defines TotalFloor as the sum of visited points across all
    /// acts (RunState.TotalFloor); delegating to it keeps every per-event
    /// Floor field consistent with the engine. Returns 0 when no run is
    /// active.
    /// </summary>
    public static int CurrentFloor
    {
        get
        {
            try { return RunManager.Instance?.DebugOnlyGetState()?.TotalFloor ?? 0; }
            catch { return 0; }
        }
    }

    // ── Recording methods (called by patches) ───────────────

    /// <summary>
    /// Record each offered card individually. pickedCardId is the one actually picked (null if skipped).
    /// </summary>
    public static void RecordCardReward(List<(string cardId, int upgradeLevel)> offeredCards, string? pickedCardId, int floor)
    {
        foreach (var (cardId, upgradeLevel) in offeredCards)
        {
            _cardChoices.Add(new CardChoiceUpload
            {
                CardId = cardId,
                UpgradeLevel = Math.Clamp(upgradeLevel, 0, 10),
                WasPicked = cardId == pickedCardId,
                Floor = floor
            });
        }
    }

    public static void RecordEventChoice(string eventId, int optionIndex, int totalOptions)
    {
        // Server bounds: option_index ∈ [-1, 20], total_options ∈ [0, 20].
        // Clamp at the boundary so an unexpected modded event with many
        // options can't poison the entire run upload.
        _eventChoices.Add(new EventChoiceUpload
        {
            EventId = eventId,
            OptionIndex = Math.Clamp(optionIndex, -1, 20),
            TotalOptions = Math.Clamp(totalOptions, 0, 20)
        });
    }

    public static void RecordShopPurchase(string itemId, string itemType, int cost, int floor)
    {
        _shopPurchases.Add(new ShopPurchaseUpload
        {
            ItemId = itemId,
            ItemType = itemType,
            Cost = Math.Clamp(cost, 0, 9999),
            Floor = floor
        });
        // Persist so save+quit+resume mid-run doesn't drop these, and so the
        // "本局统计" view (BuildSingleRunStats) can reconstruct bought-card
        // counts. The game's MapPointHistory only records BoughtColorless for
        // card buys; colored shop purchases are invisible without this.
        ShopPurchasePersistence.Save(_shopPurchases);
    }

    /// <summary>Read-only view used by RunHistoryAnalyzer.BuildSingleRunStats.</summary>
    public static IReadOnlyList<ShopPurchaseUpload> GetShopPurchases() => _shopPurchases;

    public static void RecordCardRemoval(string cardId, string source, int floor)
    {
        _cardRemovals.Add(new CardRemovalUpload
        {
            CardId = cardId,
            Source = source,
            Floor = floor
        });
    }

    public static void RecordCardUpgrade(string cardId, string source)
    {
        _cardUpgrades.Add(new CardUpgradeUpload
        {
            CardId = cardId,
            Source = source
        });
    }

    public static void RecordShopCardOffering(string cardId, int floor)
    {
        _shopCardOfferings.Add(new ShopCardOfferingUpload
        {
            CardId = cardId,
            Floor = floor
        });
        // Round 14 v5+: shop offerings are not present in the game's
        // MapPointHistory (only purchases are), so persist them to disk so
        // save+quit+resume doesn't drop them.
        ShopOfferingPersistence.Save(_shopCardOfferings);
    }

    // ── Run lifecycle ───────────────────────────────────────

    public static void OnRunStart()
    {
        _cardChoices.Clear();
        _eventChoices.Clear();
        _shopPurchases.Clear();
        _shopCardOfferings.Clear();
        _cardRemovals.Clear();
        _cardUpgrades.Clear();
        RunContributionAggregator.Instance.Reset();

        // Round 14 v5+: reload previously-recorded shop card offerings for
        // this seed (lost from in-memory list by the Clear above).
        var saved = ShopOfferingPersistence.Load();
        if (saved != null) _shopCardOfferings.AddRange(saved);

        // Same for shop purchases — needed so the "本局统计" view reconstructs
        // CardsBought across SL / resume.
        var savedPurchases = ShopPurchasePersistence.Load();
        if (savedPurchases != null) _shopPurchases.AddRange(savedPurchases);
    }

    /// <summary>
    /// Called from ModManager.OnMetricsUpload hook. Assembles and submits payload.
    /// </summary>
    public static void OnMetricsUpload(SerializableRun run, bool isVictory, ulong localPlayerId)
    {
        // Persist run-summary contributions for future Run History replay (PRD §3.12).
        // Done unconditionally — independent of upload toggle.
        Safe.Run(() =>
        {
            var seed = run?.SerializableRng?.Seed;
            if (!string.IsNullOrEmpty(seed))
            {
                // Safety net: always reassemble from per-combat disk files
                // and merge-max with in-memory totals. If live-state hydrate
                // succeeded, the disk data is a subset (no harm). If hydrate
                // failed (e.g. GetActiveSeed returned null during SL resume),
                // the disk assembly fills in the missing pre-save combats.
                // MergeMaxFrom takes the higher value per field so live data
                // (which may have progressed further) is never overwritten.
                var diskTotals = ContributionPersistence.AssembleFromCombats(seed!);
                if (diskTotals != null && diskTotals.Count > 0)
                    RunContributionAggregator.Instance.MergeMaxFrom(diskTotals);

                ContributionPersistence.SaveRunSummary(
                    seed!,
                    RunContributionAggregator.Instance.RunTotals);
                // Round 8 §3.6.1: live snapshot is no longer needed after the
                // run finishes — the per-combat and summary files cover replay.
                ContributionPersistence.DeleteLiveState(seed!);
                ShopOfferingPersistence.Delete(seed!);
                // NOTE: don't delete _shop_purchases.json here — Run History's
                // "本局统计" view loads it AFTER the run ends (including after
                // abandon). The 90-day mtime-based sweep in
                // ContributionPersistence.PruneOldFiles covers the whole
                // ContributionsDir (all *.json), so leaving this file around
                // does not create permanent clutter.
            }

            // New run finished: invalidate the cached career snapshot so the
            // next Stats screen open re-aggregates the latest data.
            RunHistoryAnalyzer.Instance.InvalidateAll();
        });

        if (!Config.ModConfig.EnableUpload)
        {
            Safe.Info("Data upload disabled by user setting, skipping.");
            return;
        }

        Safe.RunAsync(async () =>
        {
            var payload = BuildPayload(run, isVictory, localPlayerId);
            await ApiClient.Instance.UploadRunAsync(payload);
        });
    }

    private static RunUploadPayload BuildPayload(SerializableRun run, bool isVictory, ulong localPlayerId)
    {
        // PRD-04 §4.6 — privacy: localPlayerId is intentionally NOT written into
        // the upload payload. RunUploadPayload has no player_id / steam_id / user_id
        // field. The parameter is received only because the upstream hook passes it.
        // DO NOT add an identifier field to RunUploadPayload.

        var player = run.Players?.FirstOrDefault();
        var characterId = player?.CharacterId?.Entry ?? "unknown";

        var payload = new RunUploadPayload
        {
            ModVersion = ModConfig.ModVersion,
            GameVersion = VersionManager.GameVersion,
            Character = characterId,
            Ascension = run.Ascension,
            Win = isVictory,
            Branch = BranchManager.CurrentBranch,
            PlayerWinRate = ComputeLiveWinRate(isVictory),
            NumPlayers = run.Players?.Count ?? 1,
            FloorReached = CurrentFloor,
            ShopCardOfferings = new List<ShopCardOfferingUpload>(_shopCardOfferings),
            // Shop purchases: use our ShopPatch-driven list (real costs, all
            // card colors). The game's own MapPointHistory (BoughtRelics /
            // Potions / Colorless) is missing colored card buys and has
            // Cost=0 for everything, so we intentionally bypass it below.
            ShopPurchases = new List<ShopPurchaseUpload>(_shopPurchases),
            Contributions = RunContributionAggregator.Instance.BuildContributionUploads(),
        };

        // Round 14 v5+: walk SerializableRun.MapPointHistory (authoritative,
        // survives save+quit reload) to populate card/event/encounter
        // sections. Previously these lived in in-memory lists populated by
        // Record*() patches — but a mid-run save+quit drops the in-memory
        // state and the game's own MapPointHistory is the only reliable source.
        // includeShopPurchases: false — we already populated ShopPurchases
        // from our authoritative _shopPurchases list above.
        int floor = Api.HistoryImporter.PopulateFromMapHistory(
            run.MapPointHistory,
            killedByEncounter: null,
            wasWin: isVictory,
            payload,
            includeShopPurchases: false);
        if (floor > 0) payload.FloorReached = floor;

        // Final deck
        if (player?.Deck != null)
        {
            payload.FinalDeck = player.Deck.Select(c => new DeckCardUpload
            {
                CardId = c.Id?.Entry ?? "unknown",
                UpgradeLevel = Math.Clamp(c.CurrentUpgradeLevel, 0, 10)
            }).ToList();
        }

        // Final relics
        if (player?.Relics != null)
        {
            payload.FinalRelics = player.Relics
                .Select(r => r.Id?.Entry ?? "unknown")
                .ToList();
        }

        return payload;
    }

    /// <summary>
    /// Compute the player's cumulative win rate at the moment of this upload,
    /// including the current (just-finished) run. Server stores this as
    /// <c>runs.player_win_rate</c> and the F9 "min player win%" filter uses
    /// it to narrow community aggregates.
    ///
    /// Source: <see cref="RunHistoryAnalyzer"/> cached snapshot (populated by
    /// the mod's own local history walk). The snapshot is built from the
    /// game's <c>SaveManager</c> run history files — written before
    /// <c>ModManager.OnMetricsUpload</c> fires, so prior runs are visible.
    /// Whether the current run is already in the cache depends on whether
    /// <c>InvalidateAll</c> ran yet; we don't assume either way, so we
    /// compute "prior runs" by reading the cache directly and add the
    /// current run explicitly. If the cache hasn't loaded at all we compute
    /// 0-based (wr = current-run-win ? 1.0 : 0.0) — a reasonable degraded
    /// value vs. the previous hard 0.
    /// </summary>
    private static float ComputeLiveWinRate(bool isVictory)
    {
        try
        {
            var snap = RunHistoryAnalyzer.Instance.GetCached(null);
            // snap reflects the set of runs loaded prior to this call. Assume
            // the current run is NOT yet in it (InvalidateAll runs after
            // upload is queued). Add current outcome explicitly.
            int totalBefore = snap?.TotalRuns ?? 0;
            int winsBefore  = snap?.Wins ?? 0;
            int total = totalBefore + 1;
            int wins = winsBefore + (isVictory ? 1 : 0);
            return total > 0 ? (float)wins / total : 0f;
        }
        catch (Exception ex)
        {
            Safe.Warn($"[RunDataCollector] ComputeLiveWinRate failed: {ex.Message}");
            return 0f;
        }
    }
}
