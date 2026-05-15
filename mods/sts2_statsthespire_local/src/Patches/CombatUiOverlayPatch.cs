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

#if STS2_GE_V105
using CombatStateType = MegaCrit.Sts2.Core.Combat.ICombatState;
#else
using CombatStateType = MegaCrit.Sts2.Core.Combat.CombatState;
#endif

namespace CommunityStats.Patches;

/// <summary>
/// Top-bar potion-drop / card-drop odds indicators (PRD §3.9 + §3.17).
///
/// **4.18 rewrite + ChildOrderChanged hardening.**
///
/// The previous revision called <c>MoveChild</c> unconditionally every
/// time <see cref="EnsureAttachedToTopBar"/> ran, which in turn ran on
/// every Map-screen open / combat setup. Each call re-derived the Map
/// button's current index and shuffled our indicators in front of it.
/// When the game re-shuffled the top-bar HBox on its own (observed when
/// the player clicks the map icon — the game's Map button briefly moves
/// to the end of the HBox), our next pass would place the indicators
/// relative to the new Map position, dumping them to the right side.
///
/// Fix, mirroring the 4.18 DLL:
/// - A <see cref="_positioned"/> latch makes <see cref="EnsureAttachedToTopBar"/>
///   a no-op after the first successful placement for a given run. On
///   run start we reset the latch and kick off <see cref="TryAttachDeferred"/>,
///   which uses CallDeferred retries (up to <see cref="MaxAttachRetries"/>)
///   to wait for <c>NRun.GlobalUi.TopBar</c> to come online.
/// - <see cref="EnsureAttachedToTopBar"/> now returns <c>bool</c> so the
///   retry loop knows when to stop.
///
/// **User-reported residual bug (post-4.18)**: the indicators still slide
/// to the right of the Map button after the player *clicks* the map
/// icon. Root cause: the game mutates child order inside its own Map
/// handler, *after* our one-shot MoveChild finished. 4.18's latch
/// prevents us from re-shuffling redundantly, but offers no defence
/// against the game shuffling us. We add a <c>ChildOrderChanged</c>
/// subscription that re-pins our four nodes to the front of the HBox
/// whenever the host reports a reorder. Re-entry is guarded by a
/// <c>_pinning</c> flag so our own MoveChild calls don't re-trigger the
/// handler.
/// </summary>
[HarmonyPatch]
public static class CombatUiOverlayPatch
{
    private const int MaxAttachRetries = 30;

    private static PotionOddsIndicator? _potion;
    private static CardDropOddsIndicator? _cardDrop;
    private static bool _subscribed;
    private static bool _positioned;
    private static int _attachRetriesLeft;
    private static bool _orderSignalHooked;
    private static Node? _hookedHost;
    private static bool _pinning;

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

    public static void OnRunStarted()
    {
        Safe.Run(() =>
        {
            _positioned = false;
            _attachRetriesLeft = MaxAttachRetries;
            TryAttachDeferred();
        });
    }

    /// <summary>
    /// Deferred-retry loop for the attach. NRun.GlobalUi.TopBar may not be
    /// live yet on the first run-start postfix (especially for loaded saves).
    /// Each attempt returns true once the wrappers are parented and pinned;
    /// if not, we re-queue ourselves on the next frame up to 30 times.
    /// </summary>
    private static void TryAttachDeferred()
    {
        if (EnsureAttachedToTopBar())
        {
            var runState = RunManager.Instance?.DebugOnlyGetState();
            var player = runState?.Players?.FirstOrDefault();
            if (player != null) RefreshValuesFromPlayer(player);
            ShowAll();
            return;
        }
        if (_attachRetriesLeft-- <= 0) return;
        try
        {
            Callable.From(() => Safe.Run(TryAttachDeferred)).CallDeferred();
        }
        catch { }
    }

    // ── Lazy attach to NTopBar ──────────────────────────────

    /// <summary>
    /// Returns true once the indicators are live and parented to the TopBar
    /// HBox. Does MoveChild exactly once per run (gated by <see cref="_positioned"/>).
    /// Subscribes to the HBox's <c>ChildOrderChanged</c> signal the first
    /// time we successfully position so subsequent game-initiated reorders
    /// are caught by <see cref="OnHBoxChildOrderChanged"/>.
    /// </summary>
    private static bool EnsureAttachedToTopBar()
    {
        var topBar = NRun.Instance?.GlobalUi?.TopBar;
        if (topBar == null) return false;
        var mapBtn = topBar.Map;
        if (mapBtn == null || !Godot.GodotObject.IsInstanceValid(mapBtn)) return false;
        var hostHbox = mapBtn.GetParent();
        if (hostHbox == null || !Godot.GodotObject.IsInstanceValid(hostHbox)) return false;

        // Fast-path: already positioned and everything still alive.
        if (_positioned
            && IsAlive(ref _potion) && _potion!.GetParent() == hostHbox
            && IsAlive(ref _cardDrop) && _cardDrop!.GetParent() == hostHbox)
        {
            return true;
        }

        bool builtPotion = false;
        bool builtCardDrop = false;

        if (!IsAlive(ref _potion))
        {
            _potion = PotionOddsIndicator.Create();
            _potion.Visible = false;
            _potion.ZIndex = 100;
            _potion.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            hostHbox.AddChild(_potion);
            builtPotion = true;
        }
        else if (_potion!.GetParent() != hostHbox)
        {
            _potion.GetParent()?.RemoveChild(_potion);
            hostHbox.AddChild(_potion);
            builtPotion = true;
        }

        if (!IsAlive(ref _cardDrop))
        {
            _cardDrop = CardDropOddsIndicator.Create();
            _cardDrop.Visible = false;
            _cardDrop.ZIndex = 100;
            _cardDrop.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            hostHbox.AddChild(_cardDrop);
            builtCardDrop = true;
        }
        else if (_cardDrop!.GetParent() != hostHbox)
        {
            _cardDrop.GetParent()?.RemoveChild(_cardDrop);
            hostHbox.AddChild(_cardDrop);
            builtCardDrop = true;
        }

        if (builtPotion || builtCardDrop || !_positioned)
        {
            EnsureSpacer(hostHbox, "StatsTheSpireSpacerA", 18);
            EnsureSpacer(hostHbox, "StatsTheSpireSpacerB", 18);
            PinToLeftOfMap(hostHbox, mapBtn);
            HookChildOrderSignal(hostHbox);
            _positioned = true;
        }

        return true;
    }

    /// <summary>
    /// Position <c>[potion][spacerA][cardDrop][spacerB][Map]…</c>. Callable
    /// both at first attach and from the <c>ChildOrderChanged</c> handler
    /// when the game has reshuffled us elsewhere.
    /// </summary>
    private static void PinToLeftOfMap(Node hostHbox, Node mapBtn)
    {
        if (_pinning) return;
        _pinning = true;
        try
        {
            int mapIdx = mapBtn.GetIndex();
            if (mapIdx <= 0) return;
            var spacerB = hostHbox.GetNodeOrNull<Control>("StatsTheSpireSpacerB");
            var spacerA = hostHbox.GetNodeOrNull<Control>("StatsTheSpireSpacerA");
            // Order matters: MoveChild shifts the other children, so insert
            // back-to-front relative to the final visual order.
            if (spacerB != null) hostHbox.MoveChild(spacerB, mapIdx);
            if (_cardDrop != null) hostHbox.MoveChild(_cardDrop, mapIdx);
            if (spacerA != null) hostHbox.MoveChild(spacerA, mapIdx);
            if (_potion != null) hostHbox.MoveChild(_potion, mapIdx);
        }
        catch { }
        finally { _pinning = false; }
    }

    private static void HookChildOrderSignal(Node hostHbox)
    {
        // Re-subscribe if the host HBox wrapper instance changed (e.g. the
        // top bar was rebuilt between runs). Godot's event subscriptions
        // are instance-scoped, so we track the last host we hooked.
        if (_orderSignalHooked && _hookedHost == hostHbox) return;
        try
        {
            if (_orderSignalHooked && _hookedHost != null
                && Godot.GodotObject.IsInstanceValid(_hookedHost))
            {
                _hookedHost.ChildOrderChanged -= OnHBoxChildOrderChanged;
            }
            hostHbox.ChildOrderChanged += OnHBoxChildOrderChanged;
            _hookedHost = hostHbox;
            _orderSignalHooked = true;
        }
        catch { }
    }

    private static void OnHBoxChildOrderChanged()
    {
        // Ignore our own MoveChild calls.
        if (_pinning) return;
        Safe.Run(() =>
        {
            var topBar = NRun.Instance?.GlobalUi?.TopBar;
            var mapBtn = topBar?.Map;
            if (mapBtn == null || !Godot.GodotObject.IsInstanceValid(mapBtn)) return;
            var hostHbox = mapBtn.GetParent();
            if (hostHbox == null || !Godot.GodotObject.IsInstanceValid(hostHbox)) return;
            // Skip if our nodes are already in the first four positions —
            // avoids a MoveChild storm when the game inserts a transient
            // sibling (e.g. a tween proxy).
            if (IsAlive(ref _potion) && _potion!.GetIndex() == 0
                && IsAlive(ref _cardDrop) && _cardDrop!.GetIndex() == 2) return;
            PinToLeftOfMap(hostHbox, mapBtn);
        });
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

    private static bool IsAlive<T>(ref T? slot) where T : Godot.GodotObject
    {
        if (slot == null) return false;
        if (!Godot.GodotObject.IsInstanceValid(slot)) { slot = null; return false; }
        return true;
    }

    // ── Combat / data event hooks (refresh-only) ────────────

    [HarmonyPatch(typeof(CombatManager), nameof(CombatManager.SetUpCombat))]
    [HarmonyPostfix]
    public static void AfterSetUpCombat(CombatManager __instance, CombatStateType state)
    {
        Safe.Run(() =>
        {
            // If Godot reclaimed the wrappers (scene dispose between
            // combats), reset the latch so the next EnsureAttachedToTopBar
            // rebuilds and re-pins rather than returning early.
            if (!IsAlive(ref _potion) || !IsAlive(ref _cardDrop))
            {
                _positioned = false;
                EnsureAttachedToTopBar();
                ShowAll();
            }
            var me = ResolveLocalPlayer(state);
            if (me != null) RefreshValuesFromPlayer(me);
        });
    }

    /// <summary>
    /// Keep this hook for map-screen entry — it's a natural moment to
    /// verify parent / position are still correct for users who saved &amp;
    /// reloaded. The ChildOrderChanged signal makes it largely redundant
    /// for the click-the-map-icon bug, but this covers the cold-load path
    /// where the signal hasn't been hooked yet.
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
        Safe.Run(() =>
        {
            var state = CombatManager.Instance?.DebugOnlyGetState();
            if (state == null) return;
            var me = ResolveLocalPlayer(state);
            if (me != null) RefreshValuesFromPlayer(me);
        });
    }

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

    private static Player? ResolveLocalPlayer(CombatStateType state)
    {
        Player? me = null;
        try { me = LocalContext.GetMe(state); } catch { }
        if (me == null) me = state.Players?.FirstOrDefault();
        return me;
    }
}
