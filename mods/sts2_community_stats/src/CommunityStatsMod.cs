using CommunityStats.Api;
using CommunityStats.Config;
using CommunityStats.Patches;
using CommunityStats.UI;
using CommunityStats.Util;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;

namespace CommunityStats;

/// <summary>
/// Community Stats mod entry point.
/// Initializes Harmony patches, registers metrics upload hook,
/// loads user settings, and attaches UI panels to the scene tree.
/// </summary>
[ModInitializerAttribute("Initialize")]
public static class CommunityStatsMod
{
    private static Harmony? _harmony;
    private static bool _f8Pressed;
    private static bool _f9Pressed;

    public static void Initialize()
    {
        Safe.Info($"Stats the Spire v{ModConfig.ModVersion} initializing...");

        // Load config overrides (e.g. api_base_url for local testing)
        ModConfig.LoadOverrides();
        Safe.Info($"API endpoint: {ModConfig.ApiBaseUrl}");

        // Sync language setting
        L.Current = ModConfig.Language == "EN" ? L.Lang.EN : L.Lang.CN;

        // Ensure data directories exist
        ModConfig.EnsureDirectories();

        // Prune contribution snapshots older than 90 days (PRD §3.12).
        ContributionPersistence.PruneOldFiles();

        // Pre-warm the in-memory career-stats cache from disk so the very first
        // 百科大全 → 角色数据 page open shows data instantly. Background re-aggregation
        // refreshes it after a new run via RunHistoryAnalyzer.InvalidateAll().
        Safe.Run(() => Collection.RunHistoryAnalyzer.Instance.GetCached(null));

        // Round 9 round 34: kick off a background full LoadAllAsync so the
        // per-card / per-relic bundles (LocalCards / LocalRelics) get
        // populated. The disk cache only stores CareerStatsData, NOT those
        // bundles, so without this the card library and relic collection
        // would show 0 samples until the user opens the career stats screen.
        Safe.RunAsync(async () =>
        {
            try { await Collection.RunHistoryAnalyzer.Instance.LoadAllAsync(null); }
            catch (Exception ex) { Safe.Warn($"Startup LoadAllAsync failed: {ex.Message}"); }
        });

        // Round 6: pre-bake the monster intent state machines so the hover panel
        // never has to touch live combat state. Walks ModelDb.Monsters once.
        Safe.Run(() => Util.MonsterIntentMetadata.Initialize());

        // Load saved filter settings
        Safe.Run(() =>
        {
            ModConfig.CurrentFilter = FilterSettings.Load() ?? new FilterSettings();
        });

        // Load bundled test data immediately so stats are available for Neow etc.
        Safe.Run(() => StatsProvider.Instance.EnsureTestDataLoaded());

        // Apply Harmony patches — patch each class individually so one failure
        // doesn't prevent the rest from loading.
        _harmony = new Harmony("com.communitystats.sts2");
        var assembly = typeof(CommunityStatsMod).Assembly;
        foreach (var type in assembly.GetTypes())
        {
            try
            {
                var processor = _harmony.CreateClassProcessor(type);
                processor.Patch();
            }
            catch (Exception ex)
            {
                Safe.Warn($"[Harmony] Failed to patch {type.Name}: {ex}");
            }
        }

        // Apply manual hook context patches (individual try/catch per method)
        RelicHookContextPatcher.PatchAll(_harmony);
        PowerHookContextPatcher.PatchAll(_harmony);
        OrbPassivePatch.PatchOrbTurnEndTriggers(_harmony);
        OrbEvokePatch.PatchOrbEvokeMethods(_harmony);
        KillingBlowPatcher.PatchAll(_harmony);

        // PRD §3.13: subscribe label-rendering patches to StatsProvider.DataRefreshed
        // so that F9 filter Apply re-renders stats in place without reopening.
        Patches.ShopPatch.SubscribeRefresh();
        Patches.CardRewardScreenPatch.SubscribeRefresh();
        Patches.DeckViewPatch.SubscribeRefresh();
        Patches.CardLibraryPatch.SubscribeRefresh();
        Patches.RelicLibraryPatch.SubscribeRefresh();
        Patches.RelicCollectionPatch.SubscribeRefresh();
        Patches.EventOptionPatch.SubscribeRefresh();
        Patches.CardUpgradePatch.SubscribeRefresh();
        Patches.CardRemovalPatch.SubscribeRefresh();
        Patches.RelicHoverPatch.SubscribeRefresh();

        // Register ModManager.OnMetricsUpload hook for run data upload
        RunLifecyclePatch.RegisterMetricsHook();

        // Listen for filter changes to trigger data re-fetch
        FilterPanel.FilterApplied += OnFilterApplied;

        // Register hotkeys and attach UI panels to scene tree
        Safe.Run(() => RegisterHotkeys());

        // Cleanup stale disk cache
        StatsCache.Instance.CleanupDisk();

        // PRD §3.19: one-time import of local run history files
        HistoryImporter.TryImportAsync();

        // Drain any pending offline uploads from previous sessions. Without
        // this, runs queued before a server outage would only retry on the
        // next successful real-time upload — i.e. never if the player
        // doesn't finish another run after restart.
        Safe.RunAsync(() => Util.OfflineQueue.DrainAsync(
            json => Api.ApiClient.Instance.PostJsonWithStatusAsync("runs", json)));

        Safe.Info("Stats the Spire initialized successfully");
    }

    private static void OnFilterApplied()
    {
        Safe.Info("[DIAG:OnFilterApplied] Event handler entered");
        Safe.RunAsync(async () =>
        {
            // PRD §3.18 — re-resolve the character from the new filter mode so
            // changing the F9 character dropdown takes effect immediately.
            var filter = ModConfig.CurrentFilter;
            var resolvedChar = filter.ResolveCharacter();
            var ver = VersionManager.GetEffectiveVersion(filter);
            Safe.Info($"[DIAG:OnFilterApplied] resolvedChar={resolvedChar}, GameVersion={filter.GameVersion}, effectiveVer={ver}, qs={filter.ToQueryString()}");
            await StatsProvider.Instance.OnFilterChangedAsync(resolvedChar, filter);
            FilterPanel.Instance.UpdateSampleSizeLabel();

            // Round 9 round 33: when feature toggles flip, immediately strip
            // any UI that the affected feature already attached so the user
            // doesn't have to navigate away and back.
            Safe.Run(RefreshVisibleFeatureUi);
        });
    }

    /// <summary>
    /// Walk the live scene tree and clean up UI elements whose owning feature
    /// toggle is now off (called from OnFilterApplied). Currently handles map
    /// point danger overlays + compendium card stats panel — both leave
    /// children in place after their patches early-return on toggle off.
    /// </summary>
    private static void RefreshVisibleFeatureUi()
    {
        var tree = Engine.GetMainLoop() as SceneTree;
        var root = tree?.Root;
        if (root == null) return;

        bool dangerOff = !ModConfig.Toggles.MonsterDanger;
        bool cardStatsOff = !ModConfig.Toggles.CardLibraryStats;

        WalkAndStrip(root, dangerOff, cardStatsOff);
    }

    private static void WalkAndStrip(Node node, bool dangerOff, bool cardStatsOff)
    {
        // Map danger overlays (StatsLabel children with cs_overlay meta on
        // NMapPoint) — DetachFrom is no-op when meta is missing so safe.
        if (dangerOff && node is MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapPoint mp)
        {
            UI.MapPointOverlay.DetachFrom(mp);
        }

        // Compendium card library panel — InjectOrUpdate uses the
        // "StatsTheSpireCardStats" name; queue-free it on toggle off.
        if (cardStatsOff && node.Name.ToString() == "StatsTheSpireCardStats")
        {
            node.QueueFree();
            return;
        }

        foreach (var child in node.GetChildren())
            WalkAndStrip(child, dangerOff, cardStatsOff);
    }

    private static void RegisterHotkeys()
    {
        // Wait for scene tree, then use ProcessFrame signal for input polling.
        Safe.RunAsync(async () =>
        {
            while (Engine.GetMainLoop() is not SceneTree sceneTree || sceneTree.Root == null)
            {
                await Task.Delay(100);
            }

            var tree = (SceneTree)Engine.GetMainLoop();
            tree.ProcessFrame += OnProcessFrame;

            // Add UI panels to root
            var root = tree.Root;
            root.CallDeferred(Node.MethodName.AddChild, ContributionPanel.Instance);
            root.CallDeferred(Node.MethodName.AddChild, FilterPanel.Instance);

            // Attach top-bar potion / card-drop indicators (round 5 fix:
            // they used to be parented to NCombatUi which never showed them).
            CommunityStats.Patches.CombatUiOverlayPatch.Attach();
        });
    }

    private static void OnProcessFrame()
    {
        Safe.Run(() =>
        {
            // F8 = Toggle contribution panel
            bool f8Now = Input.IsKeyPressed(Key.F8);
            if (f8Now && !_f8Pressed)
            {
                ContributionPanel.Toggle();
            }
            _f8Pressed = f8Now;

            // F9 = Toggle settings/filter panel
            bool f9Now = Input.IsKeyPressed(Key.F9);
            if (f9Now && !_f9Pressed)
            {
                FilterPanel.Toggle();
            }
            _f9Pressed = f9Now;
        });
    }
}
