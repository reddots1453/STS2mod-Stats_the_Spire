using CommunityStats.Api;
using CommunityStats.Collection;
using CommunityStats.Config;
using CommunityStats.Util;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Runs;

namespace CommunityStats.Patches;

/// <summary>
/// Patches RunManager to trigger data preload at run start and upload at run end.
/// Also hooks ModManager.OnMetricsUpload for run data submission.
/// </summary>
[HarmonyPatch]
public static class RunLifecyclePatch
{
    [HarmonyPatch(typeof(RunManager), nameof(RunManager.SetUpNewSinglePlayer))]
    [HarmonyPostfix]
    public static void OnRunStartSP(RunManager __instance, RunState state)
    {
        Safe.Run(() =>
        {
            Safe.Info("[DIAG:RunLifecycle] SetUpNewSinglePlayer Postfix fired");
            RunDataCollector.OnRunStart();
            TryHydrateLiveState();
            // PRD §3.9 / §3.17 round 9: top-bar indicators get built once
            // per run (lifetime mirrors NTopBar's). Defer one frame so
            // NRun.GlobalUi.TopBar is fully laid out by the time we attach.
            CommunityStats.Patches.CombatUiOverlayPatch.OnRunStarted();

            var players = state.Players;
            Safe.Info($"[DIAG:RunLifecycle] state.Players={players != null}, count={players?.Count}");

            var player = players?.FirstOrDefault();
            Safe.Info($"[DIAG:RunLifecycle] player={player != null}");

            var runCharacter = player?.Character?.Id.Entry;
            Safe.Info($"[DIAG:RunLifecycle] character={runCharacter ?? "NULL"}");

            if (runCharacter != null)
            {
                // PRD §3.18 — preload uses the *filter-resolved* character so a
                // user who has manually pinned (e.g.) "SILENT" still sees Silent
                // data when starting an Ironclad run. Falls back to the run's
                // own character only when the filter resolves to null.
                var filter = ModConfig.CurrentFilter;
                var preloadChar = filter.ResolveCharacter() ?? runCharacter;
                Safe.Info($"[DIAG:RunLifecycle] Launching PreloadForRunAsync for {preloadChar} (mode={filter.CharacterFilterMode})");
                Safe.RunAsync(() =>
                    StatsProvider.Instance.PreloadForRunAsync(preloadChar, filter));
            }
            else
            {
                Safe.Warn("[DIAG:RunLifecycle] character is null, skipping preload");
            }
        });
    }

    /// <summary>
    /// Round 9 fix: when the user clicks "Continue" from the main menu the
    /// game routes through `SetUpSavedSinglePlayer`, NOT `SetUpNewSinglePlayer`.
    /// Without this hook the run-start path never fires for resumed runs and
    /// every per-run init (live-state hydrate, top-bar indicators) is skipped.
    /// Crash dump analysis showed the second crash was in the resume path
    /// because `CombatUiOverlayPatch.OnRunStarted` was never called →
    /// `_potion / _cardDrop` stayed null → `AfterSetUpCombat` fallback tried
    /// to lazy-create them mid-combat-init and AV'd inside the Godot binding.
    /// </summary>
    [HarmonyPatch(typeof(RunManager), nameof(RunManager.SetUpSavedSinglePlayer))]
    [HarmonyPostfix]
    public static void OnRunResumeSP(RunManager __instance, RunState state)
    {
        Safe.Run(() =>
        {
            Safe.Info("[DIAG:RunLifecycle] SetUpSavedSinglePlayer Postfix fired");
            RunDataCollector.OnRunStart();
            TryHydrateLiveState();
            CommunityStats.Patches.CombatUiOverlayPatch.OnRunStarted();

            var player = state?.Players?.FirstOrDefault();
            var runCharacter = player?.Character?.Id.Entry;
            if (runCharacter != null)
            {
                var filter = ModConfig.CurrentFilter;
                var preloadChar = filter.ResolveCharacter() ?? runCharacter;
                Safe.RunAsync(() =>
                    StatsProvider.Instance.PreloadForRunAsync(preloadChar, filter));
            }
        });
    }

    [HarmonyPatch(typeof(RunManager), nameof(RunManager.SetUpSavedMultiPlayer))]
    [HarmonyPostfix]
    public static void OnRunResumeMP(RunManager __instance, RunState state)
    {
        Safe.Run(() =>
        {
            Safe.Info("[DIAG:RunLifecycle] SetUpSavedMultiPlayer Postfix fired");
            RunDataCollector.OnRunStart();
            TryHydrateLiveState();
            CommunityStats.Patches.CombatUiOverlayPatch.OnRunStarted();
        });
    }

    [HarmonyPatch(typeof(RunManager), nameof(RunManager.SetUpNewMultiPlayer))]
    [HarmonyPostfix]
    public static void OnRunStartMP(RunManager __instance, RunState state)
    {
        Safe.Run(() =>
        {
            RunDataCollector.OnRunStart();
            TryHydrateLiveState();
            CommunityStats.Patches.CombatUiOverlayPatch.OnRunStarted();

            // PRD §3.15 — multiplayer compat: prefer the local player; fall back to
            // Players[0] only if LocalContext is unavailable. We must never crash
            // when state or its Players list is null.
            if (state == null) return;

            MegaCrit.Sts2.Core.Entities.Players.Player? player = null;
            try { player = MegaCrit.Sts2.Core.Context.LocalContext.GetMe(state); } catch { }
            if (player == null) player = state.Players?.FirstOrDefault();

            var runCharacter = player?.Character?.Id.Entry;
            if (runCharacter != null)
            {
                var filter = ModConfig.CurrentFilter;
                var preloadChar = filter.ResolveCharacter() ?? runCharacter;
                Safe.RunAsync(() =>
                    StatsProvider.Instance.PreloadForRunAsync(preloadChar, filter));
            }
        });
    }

    /// <summary>
    /// Round 8 §3.6.1: when a run starts (or resumes after save+quit),
    /// check for a `_live.json` snapshot for that seed and rehydrate the
    /// CombatTracker / RunContributionAggregator from it.
    /// </summary>
    private static void TryHydrateLiveState()
    {
        try
        {
            var seed = Util.ContributionPersistence.GetActiveSeed();
            if (string.IsNullOrEmpty(seed)) return;
            var snap = Util.ContributionPersistence.LoadLiveState(seed!);
            if (snap == null) return;
            CombatTracker.Instance.HydrateFromLiveSnapshot(snap);
            Safe.Info($"[RunLifecycle] hydrated live state for seed={seed} (combatInProgress={snap.CombatInProgress}, runTotalEntries={snap.RunTotals?.Count ?? 0})");
        }
        catch (System.Exception ex)
        {
            Safe.Warn($"TryHydrateLiveState failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Register the OnMetricsUpload hook. Called once at mod init.
    /// </summary>
    public static void RegisterMetricsHook()
    {
        ModManager.OnMetricsUpload += (run, isVictory, localPlayerId) =>
        {
            Safe.Run(() => RunDataCollector.OnMetricsUpload(run, isVictory, localPlayerId));
        };
    }

    /// <summary>
    /// Round 9 round 36: hook the authoritative run-history-write path.
    /// `OnMetricsUpload` is **skipped** by the game when the run was abandoned
    /// (see MetricUtilities.UploadRunMetricsInternal), so we'd never invalidate
    /// our cache for abandoned runs even though the .run file IS written.
    /// `RunHistoryUtilities.CreateRunHistoryEntry` runs for ALL run-end paths
    /// (death / victory / abandon-from-main-menu) and is the function that
    /// actually calls `SaveManager.SaveRunHistory`, so postfixing it gives
    /// us a guaranteed signal to refresh local bundles.
    /// </summary>
    [HarmonyPatch(typeof(MegaCrit.Sts2.Core.Runs.RunHistoryUtilities),
        nameof(MegaCrit.Sts2.Core.Runs.RunHistoryUtilities.CreateRunHistoryEntry))]
    [HarmonyPostfix]
    public static void AfterCreateRunHistoryEntry(
        MegaCrit.Sts2.Core.Saves.SerializableRun run, bool victory, bool isAbandoned)
    {
        Safe.Run(() =>
        {
            Safe.Info($"[RunLifecycle] CreateRunHistoryEntry postfix: victory={victory}, abandoned={isAbandoned}");

            // Abandoned runs are skipped by OnMetricsUpload, so upload here.
            if (isAbandoned)
            {
                Safe.Info("[RunLifecycle] Abandoned run — uploading via CreateRunHistoryEntry");
                Collection.RunDataCollector.OnMetricsUpload(run, false, 0);
            }

            // Drop cached snapshot + bundles, then kick off a forced reload
            // so the next time the player opens the card library / relic
            // collection / career stats screen, fresh data is ready.
            Collection.RunHistoryAnalyzer.Instance.InvalidateAll();
            Safe.RunAsync(async () =>
            {
                try
                {
                    await Collection.RunHistoryAnalyzer.Instance.LoadAllAsync(null, force: true);
                }
                catch (System.Exception ex)
                {
                    Safe.Warn($"[RunLifecycle] post-run reload failed: {ex.Message}");
                }
            });
        });
    }
}
