using CommunityStats.Config;
using CommunityStats.Util;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace CommunityStats.Collection;

/// <summary>
/// Tracks local shop data during a run and handles run-end contribution
/// persistence. All server upload logic has been removed.
/// </summary>
public static class RunDataCollector
{
    private static readonly List<LocalShopPurchase> _shopPurchases = new();
    private static readonly List<LocalShopCardOffering> _shopCardOfferings = new();

    /// <summary>
    /// Live floor count derived from the run state's MapPointHistory.
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

    public static void RecordShopPurchase(string itemId, string itemType, int cost, int floor)
    {
        _shopPurchases.Add(new LocalShopPurchase
        {
            ItemId = itemId,
            ItemType = itemType,
            Cost = Math.Clamp(cost, 0, 9999),
            Floor = floor
        });
        ShopPurchasePersistence.Save(_shopPurchases);
    }

    /// <summary>Read-only view used by RunHistoryAnalyzer.BuildSingleRunStats.</summary>
    public static IReadOnlyList<LocalShopPurchase> GetShopPurchases() => _shopPurchases;

    public static void RecordShopCardOffering(string cardId, int floor)
    {
        _shopCardOfferings.Add(new LocalShopCardOffering
        {
            CardId = cardId,
            Floor = floor
        });
        ShopOfferingPersistence.Save(_shopCardOfferings);
    }

    // ── Deprecated recording methods (no-ops, kept for ABI compat) ──

    public static void RecordCardReward(List<(string cardId, int upgradeLevel)> offeredCards, string? pickedCardId, int floor) { }
    public static void RecordEventChoice(string eventId, int optionIndex, int totalOptions) { }
    public static void RecordCardRemoval(string cardId, string source, int floor) { }
    public static void RecordCardUpgrade(string cardId, string source) { }

    // ── Run lifecycle ───────────────────────────────────────

    public static void OnRunStart()
    {
        _shopPurchases.Clear();
        _shopCardOfferings.Clear();
        RunContributionAggregator.Instance.Reset();

        // Reload persisted shop data for this seed (survives save+quit+resume).
        var savedOffers = ShopOfferingPersistence.Load();
        if (savedOffers != null) _shopCardOfferings.AddRange(savedOffers);

        var savedPurchases = ShopPurchasePersistence.Load();
        if (savedPurchases != null) _shopPurchases.AddRange(savedPurchases);
    }

    /// <summary>
    /// Called from ModManager.OnMetricsUpload hook. Saves run-summary
    /// contributions for future Run History replay (PRD §3.12). No upload.
    /// </summary>
    public static void OnMetricsUpload(SerializableRun run, bool isVictory, ulong localPlayerId)
    {
        Safe.Run(() =>
        {
            var seed = run?.SerializableRng?.Seed;
            if (!string.IsNullOrEmpty(seed))
            {
                // Merge disk data with in-memory totals for contribution persistence.
                var diskTotals = ContributionPersistence.AssembleFromCombats(seed!);
                if (diskTotals != null && diskTotals.Count > 0)
                    RunContributionAggregator.Instance.MergeMaxFrom(diskTotals);

                ContributionPersistence.SaveRunSummary(
                    seed!,
                    RunContributionAggregator.Instance.RunTotals);
                ContributionPersistence.DeleteLiveState(seed!);
                ShopOfferingPersistence.Delete(seed!);
            }

            // Invalidate cached career snapshot so the next stats screen
            // open re-aggregates the latest data.
            RunHistoryAnalyzer.Instance.InvalidateAll();
        });
    }
}
