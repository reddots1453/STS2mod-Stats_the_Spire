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
/// Stats the Spire (Local Edition) mod entry point.
/// Initializes Harmony patches, loads user settings, and attaches UI panels
/// to the scene tree. No network — all data is local.
/// </summary>
[ModInitializerAttribute("Initialize")]
public static class CommunityStatsMod
{
    private static Harmony? _harmony;
    private static bool _f7Pressed;
    private static bool _f8Pressed;

    public static void Initialize()
    {
        Safe.Info($"Stats the Spire (Local) v{ModConfig.ModVersion} initializing...");

        Updater.Instance.Edition = "local";

        // Load saved settings (feature toggles, language, etc.) from disk.
        ModConfig.LoadOverrides();

        // Sync language setting
        L.Current = ModConfig.Language == "EN" ? L.Lang.EN : L.Lang.CN;

        // Ensure data directories exist
        ModConfig.EnsureDirectories();

        // Background: check for mod updates.
        Safe.Info("[Updater] Kicking off background update check...");
        _ = Updater.Instance.CheckForUpdateAsync();

        // Prune contribution snapshots older than 90 days.
        ContributionPersistence.PruneOldFiles();

        // Pre-warm the in-memory career-stats cache from disk so the very first
        // 百科大全 → 角色数据 page open shows data instantly.
        Safe.Run(() => Collection.RunHistoryAnalyzer.Instance.GetCached(null));

        // Kick off a background full LoadAllAsync so the per-card / per-relic
        // bundles (LocalCards / LocalRelics) get populated.
        Safe.RunAsync(async () =>
        {
            try { await Collection.RunHistoryAnalyzer.Instance.LoadAllAsync(null); }
            catch (Exception ex) { Safe.Warn($"Startup LoadAllAsync failed: {ex.Message}"); }
        });

        // Pre-bake the monster intent state machines so the hover panel
        // never has to touch live combat state.
        Safe.Run(() => Util.MonsterIntentMetadata.Initialize());

        // Load saved filter settings
        Safe.Run(() =>
        {
            ModConfig.CurrentFilter = FilterSettings.Load() ?? new FilterSettings();
        });

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

        // Register ModManager.OnMetricsUpload hook for run contribution saving
        RunLifecyclePatch.RegisterMetricsHook();

        // Language-only changes: re-render all visible UI immediately.
        Config.L.LanguageChanged += () =>
        {
            Safe.Info("[LangChanged] triggering immediate UI re-render");
        };

        // Register hotkeys and attach UI panels to scene tree
        Safe.Run(() => RegisterHotkeys());

        Safe.Info("Stats the Spire initialized successfully");
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

            // Attach top-bar potion / card-drop indicators.
            CommunityStats.Patches.CombatUiOverlayPatch.Attach();
        });
    }

    private static void OnProcessFrame()
    {
        Safe.Run(() =>
        {
            // F7 = Toggle settings panel
            bool f7Now = Input.IsKeyPressed(Key.F7);
            if (f7Now && !_f7Pressed)
            {
                FilterPanel.Toggle();
            }
            _f7Pressed = f7Now;

            // F8 = Toggle contribution panel
            bool f8Now = Input.IsKeyPressed(Key.F8);
            if (f8Now && !_f8Pressed)
            {
                ContributionPanel.Toggle();
            }
            _f8Pressed = f8Now;
        });
    }
}
