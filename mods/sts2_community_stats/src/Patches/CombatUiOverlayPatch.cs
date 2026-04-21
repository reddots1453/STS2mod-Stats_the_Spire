using System.Linq;
using CommunityStats.Collection;
using CommunityStats.Config;
using CommunityStats.UI;
using CommunityStats.Util;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace CommunityStats.Patches;

/// <summary>
/// Top-bar potion-drop / card-drop odds indicators (PRD §3.9 + §3.17).
///
/// **Round 9 lifecycle rewrite (dump-driven crash fix):**
///
/// History:
/// - Round 5 attached the indicators to the scene root and used a SetUpCombat
///   postfix to toggle visibility per combat.
/// - Round 7 reparented to `NTopBar.Map.GetParent()` so they sat in the native
///   top-bar HBox at proper size.
/// - The user reported a hard crash in `SlayTheSpire2.exe + 0xebf76b`
///   (`EXCEPTION_ACCESS_VIOLATION` reading NULL+0x68) on the second combat of
///   a run. Dump analysis showed it was triggered through this patch — the
///   static `_potion / _cardDrop` fields outlive the NTopBar HBox they were
///   parented under, so when Godot disposed the previous combat scene the
///   indicators' native instances were freed; the next `EnsureAttachedToTopBar`
///   call dereferenced the dead wrapper inside `_potion.GetParent()` and AV'd.
///
/// PRD §3.9 / §3.17 round 9 mandate (user clarification):
/// - Visibility and lifetime mirror the **native NTopBar Map / Deck buttons
///   exactly** — NTopBar exists and is visible → indicators exist and are
///   visible; NTopBar is freed → indicators are freed too. We achieve this
///   by parenting the indicators as siblings of `NTopBar.Map` inside the
///   same HBox; Godot's parent-child contract guarantees the children are
///   QueueFree'd whenever the parent NTopBar HBox is destroyed.
/// - Created **once** per run on `RunManager.SetUpNewSinglePlayer / MultiPlayer`
///   postfix (NTopBar is guaranteed live by then).
/// - **No manual destruction.** Godot reclaims them with NTopBar; the next
///   `OnRunStarted` call sees `IsInstanceValid = false`, nulls the static
///   slots, and lazy-creates fresh wrappers for the new run.
/// - Combat events (`SetUpCombat`, `OnCombatEnded`, `Resume`,
///   `CombatTracker.CombatDataUpdated`) only update the displayed numbers.
///   They NEVER touch parent / child / index / Visible — visibility flows
///   from the parent HBox automatically.
/// - Every access to `_potion` / `_cardDrop` is gated through
///   `IsAlive(...)` which calls `Godot.GodotObject.IsInstanceValid` and
///   nulls the field if the wrapper has been freed.
/// - The feature toggles (`PotionDropOdds`, `CardDropOdds`) still gate the
///   indicators' own `Visible` property — disabling the toggle hides the
///   single indicator without affecting siblings.
/// </summary>
[HarmonyPatch]
public static class CombatUiOverlayPatch
{
    private static PotionOddsIndicator? _potion;
    private static CardDropOddsIndicator? _cardDrop;
    private static bool _subscribed;

    /// <summary>
    /// Subscribe to live data updates once at mod init. The actual node
    /// instances are not created here — they're built when a run starts via
    /// `EnsureAttachedToTopBar`. NTopBar doesn't exist at mod init so any
    /// attempt to create + parent here would silently fail.
    /// </summary>
    public static void Attach()
    {
        Safe.Run(() =>
        {
            if (!_subscribed)
            {
                CombatTracker.Instance.CombatDataUpdated += OnCombatDataUpdated;
                _subscribed = true;
            }
        });
    }

    // ── Run lifecycle hooks ─────────────────────────────────

    /// <summary>
    /// Called from `RunLifecyclePatch.OnRunStartSP/MP` postfix once a run
    /// (single-player or multi) is fully constructed. Builds the indicator
    /// nodes, parents them to the native top-bar HBox, primes their values,
    /// and shows them. Idempotent — if a previous attach already succeeded
    /// for this run we just refresh values.
    /// </summary>
    public static void OnRunStarted()
    {
        Safe.Run(() =>
        {
            EnsureAttachedToTopBar();
            // Prime values from the current run state.
            var runState = RunManager.Instance?.DebugOnlyGetState();
            var player = runState?.Players?.FirstOrDefault();
            if (player != null) RefreshValuesFromPlayer(player);
            // Indicators are now visible for the entire run.
            ShowAll();
        });
    }

    // No OnRunEnded hook — Godot's parent-child contract handles cleanup
    // automatically. When NTopBar's HBox is freed (run ended → main menu)
    // every child including our indicators is QueueFree'd by the engine.
    // The next OnRunStarted call sees IsInstanceValid=false, nulls the
    // static slots, and rebuilds fresh wrappers for the new run.

    // ── Lazy attach to NTopBar (run-start only) ─────────────

    /// <summary>
    /// Build the indicator nodes (if not yet built or freed) and parent
    /// them to the same HBox that holds NTopBar.Map / NTopBar.Deck so they
    /// live in the natural top-bar flow with matching size and position.
    /// </summary>
    private static void EnsureAttachedToTopBar()
    {
        var topBar = NRun.Instance?.GlobalUi?.TopBar;
        if (topBar == null) return;
        var mapBtn = topBar.Map;
        if (mapBtn == null || !Godot.GodotObject.IsInstanceValid(mapBtn)) return;
        var hostHbox = mapBtn.GetParent();
        if (hostHbox == null || !Godot.GodotObject.IsInstanceValid(hostHbox)) return;

        // Recreate any indicator whose wrapper is dead before reading from it.
        // Round 9 round 3 (alignment fix): force `SizeFlagsVertical = ShrinkCenter`
        // so the indicators sit at the same vertical baseline as the native
        // Map / Deck buttons inside the host HBox (default is Top, which made
        // them appear above the native buttons in the user's screenshot).
        if (!IsAlive(ref _potion))
        {
            _potion = PotionOddsIndicator.Create();
            _potion.Visible = false;
            _potion.ZIndex = 100;
            _potion.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            hostHbox.AddChild(_potion);
        }
        else if (_potion!.GetParent() != hostHbox)
        {
            // Wrapper alive but attached elsewhere — reparent.
            _potion.GetParent()?.RemoveChild(_potion);
            hostHbox.AddChild(_potion);
        }
        else
        {
            // Same parent: still ensure alignment is correct (cheap idempotent set).
            _potion.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        }

        if (!IsAlive(ref _cardDrop))
        {
            _cardDrop = CardDropOddsIndicator.Create();
            _cardDrop.Visible = false;
            _cardDrop.ZIndex = 100;
            _cardDrop.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            hostHbox.AddChild(_cardDrop);
        }
        else if (_cardDrop!.GetParent() != hostHbox)
        {
            _cardDrop.GetParent()?.RemoveChild(_cardDrop);
            hostHbox.AddChild(_cardDrop);
        }
        else
        {
            _cardDrop.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        }

        // Round 9 round 3: insert two narrow spacer Controls so the visual
        // gap between potion → cardDrop → Map is +50% wider than the host
        // HBox's default theme separation. We can't override the host's
        // separation constant (it's owned by the game), so we add explicit
        // spacers as siblings.
        EnsureSpacer(hostHbox, "StatsTheSpireSpacerA", 18);
        EnsureSpacer(hostHbox, "StatsTheSpireSpacerB", 18);

        // Position both indicators + spacers BEFORE the Map button:
        //   [potion] [spacerA] [cardDrop] [spacerB] [Map] [Deck] ...
        try
        {
            var mapIdx = mapBtn.GetIndex();
            var spacerB = hostHbox.GetNodeOrNull<Control>("StatsTheSpireSpacerB");
            var spacerA = hostHbox.GetNodeOrNull<Control>("StatsTheSpireSpacerA");
            if (mapIdx > 0)
            {
                if (spacerB != null) hostHbox.MoveChild(spacerB, mapIdx);
                hostHbox.MoveChild(_cardDrop!, mapIdx);
                if (spacerA != null) hostHbox.MoveChild(spacerA, mapIdx);
                hostHbox.MoveChild(_potion!, mapIdx);
            }
        }
        catch { }
    }

    private static void EnsureSpacer(Node hostHbox, string name, float widthPx)
    {
        var existing = hostHbox.GetNodeOrNull<Control>(name);
        if (existing != null && Godot.GodotObject.IsInstanceValid(existing)) return;
        var spacer = new Control
        {
            Name = name,
            CustomMinimumSize = new Vector2(widthPx, 0),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        hostHbox.AddChild(spacer);
    }

    /// <summary>
    /// Validate the wrapper is still backed by a live native instance. If
    /// the native side has been freed (Godot disposing the parent scene
    /// reclaimed it), null the slot so callers know to rebuild.
    /// </summary>
    private static bool IsAlive<T>(ref T? slot) where T : Godot.GodotObject
    {
        if (slot == null) return false;
        if (!Godot.GodotObject.IsInstanceValid(slot)) { slot = null; return false; }
        return true;
    }

    // ── Combat / data event hooks (refresh-only) ────────────

    [HarmonyPatch(typeof(CombatManager), nameof(CombatManager.SetUpCombat))]
    [HarmonyPostfix]
    public static void AfterSetUpCombat(CombatManager __instance, CombatState state)
    {
        Safe.Run(() =>
        {
            // Round 9 round 2: re-add the lazy attach fallback because the
            // SetUpSavedSinglePlayer hook fires BEFORE NRun.GlobalUi.TopBar
            // is fully constructed — by SetUpCombat time the top bar
            // definitely exists, so this is the right late-binding point.
            // The previous round's crash that motivated removing this path
            // was diagnosed via bisection to IntentHoverPatch (now fixed
            // with CallDeferred), not this fallback.
            if (!IsAlive(ref _potion) || !IsAlive(ref _cardDrop))
            {
                EnsureAttachedToTopBar();
                ShowAll();
            }
            var me = ResolveLocalPlayer(state);
            if (me != null) RefreshValuesFromPlayer(me);
        });
    }

    /// <summary>
    /// Round 15: attach indicators the moment the player opens the map
    /// screen, so they appear before the first combat (previously the only
    /// fallback was <see cref="AfterSetUpCombat"/>, which ran on combat
    /// entry and left the indicators missing while the player was still
    /// browsing the map). NMapScreen.Open is called every time the player
    /// transitions to the map view (fresh run, between combats, after
    /// shop / event / rest, resume from save). Idempotent — the
    /// EnsureAttachedToTopBar / IsAlive guards skip work when the wrappers
    /// are already attached.
    /// </summary>
    [HarmonyPatch(typeof(MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen),
        nameof(MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen.Open))]
    [HarmonyPostfix]
    public static void AfterMapScreenOpen()
    {
        Safe.Run(() =>
        {
            EnsureAttachedToTopBar();
            ShowAll();
            var runState = RunManager.Instance?.DebugOnlyGetState();
            var player = runState?.Players?.FirstOrDefault();
            if (player != null) RefreshValuesFromPlayer(player);
        });
    }

    [HarmonyPatch(typeof(CombatRoom), nameof(CombatRoom.OnCombatEnded))]
    [HarmonyPostfix]
    public static void AfterCombatEnded(CombatRoom __instance)
    {
        // PRD §3.9 / §3.17 round 9: do NOT hide on combat end. Indicators
        // remain visible across map / shop / event / reward screens. We
        // only refresh values here so combat-driven pity changes show up
        // immediately on the next non-combat screen.
        Safe.Run(() =>
        {
            var state = CombatManager.Instance?.DebugOnlyGetState();
            if (state == null) return;
            var me = ResolveLocalPlayer(state);
            if (me != null) RefreshValuesFromPlayer(me);
        });
    }

    /// <summary>
    /// Round 9 fix: when a saved run is resumed mid-combat, `SetUpCombat`
    /// is NOT called again, so we re-prime values here. The lifecycle hook
    /// `OnRunStarted` covers the wrapper creation case (it fires on the
    /// run-start postfix that runs before Resume).
    /// </summary>
    [HarmonyPatch(typeof(CombatRoom), nameof(CombatRoom.Resume))]
    [HarmonyPostfix]
    public static void AfterCombatRoomResume(CombatRoom __instance)
    {
        Safe.Run(() =>
        {
            var state = CombatManager.Instance?.DebugOnlyGetState();
            if (state == null) return;
            var me = ResolveLocalPlayer(state);
            if (me != null) RefreshValuesFromPlayer(me);
        });
    }

    private static void OnCombatDataUpdated()
    {
        Safe.Run(() =>
        {
            var state = CombatManager.Instance?.DebugOnlyGetState();
            if (state == null) return;
            var me = ResolveLocalPlayer(state);
            if (me != null) RefreshValuesFromPlayer(me);
        });
    }

    // ── Helpers ─────────────────────────────────────────────

    private static void ShowAll()
    {
        if (IsAlive(ref _potion)) _potion!.Visible = true;
        if (IsAlive(ref _cardDrop)) _cardDrop!.Visible = true;
    }

    private static void RefreshValuesFromPlayer(Player me)
    {
        if (IsAlive(ref _potion) && _potion!.Visible)
        {
            try
            {
                var v = me.PlayerOdds?.PotionReward?.CurrentValue ?? 0.4f;
                _potion.UpdateOdds((float)v);
            }
            catch { }
        }
        if (IsAlive(ref _cardDrop) && _cardDrop!.Visible)
        {
            try
            {
                var v = me.PlayerOdds?.CardRarity?.CurrentValue ?? -0.05f;
                _cardDrop.UpdateOffset((float)v);
            }
            catch { }
        }
    }

    private static Player? ResolveLocalPlayer(CombatState state)
    {
        // Prefer the local-context player; fall back to first player so
        // single-player runs (where NetId may not be set yet) still work.
        Player? me = null;
        try { me = LocalContext.GetMe(state); } catch { }
        if (me == null) me = state.Players?.FirstOrDefault();
        return me;
    }
}
