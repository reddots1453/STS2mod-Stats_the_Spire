using CommunityStats.Config;
using CommunityStats.UI;
using CommunityStats.Util;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Nodes.Combat;

namespace CommunityStats.Patches;

/// <summary>
/// Hover trigger for the intent state machine panel (PRD §3.10).
///
/// **Round 9 round 2 — post-bisect rewrite (deterministic crash fix):**
/// Three crash dumps (Apr 10 19:23 / 19:49 / 20:09) all hit the same
/// instruction `SlayTheSpire2.exe + 0xebf76b` reading NULL+0x68 in the main
/// UI thread, with a call stack walking through `USER32!DispatchMessageW →
/// Godot WndProc`. Bisection (disabling this patch entirely) confirmed the
/// crash originated from the postfix logic running inside the Win32
/// message-dispatch chain — touching the Godot scene tree (AddChild,
/// SetMeta, GlobalPosition) from inside that callback enters game native
/// code that doesn't tolerate scene-tree mutation while a focus / mouse
/// event is being dispatched.
///
/// **The fix is to defer ALL scene-tree work to the next frame** via
/// `CallDeferred` on a SceneTree-rooted callback. The postfix itself only
/// records the creature reference and schedules the deferred run; by the
/// time the deferred handler executes, the WndProc dispatch has unwound
/// and the engine is back in its safe `_process` phase.
///
/// Additional hardening:
/// - `IsInstanceValid` guard before every Godot binding call.
/// - All work wrapped in try/catch via `Safe.Run`.
/// - `Pending*` static fields hold deferred-target state so the deferred
///   handler can reach the right NCreature even if the postfix's local
///   reference would otherwise be GC'd.
/// </summary>
[HarmonyPatch]
public static class IntentHoverPatch
{
    private const string PanelMeta = "sts_intent_state_panel";

    // Round 9 round 2: pending creature for the next-frame deferred call.
    // Only one hover at a time matters — newer hovers overwrite the slot.
    private static NCreature? _pendingShow;
    private static NCreature? _pendingHide;
    private static bool _showQueued;
    private static bool _hideQueued;

    [HarmonyPatch(typeof(NCreature), "OnFocus")]
    [HarmonyPostfix]
    public static void AfterCreatureFocus(NCreature __instance)
    {
        if (!ModConfig.Toggles.IntentStateMachine) return;
        if (__instance == null) return;
        // Round 9 round 2: do NOT touch the scene tree synchronously.
        // Stash the target and defer to next frame.
        _pendingShow = __instance;
        if (_showQueued) return;
        _showQueued = true;
        try
        {
            var tree = Engine.GetMainLoop() as SceneTree;
            tree?.Root?.CallDeferred(Node.MethodName.PropagateNotification, (long)Node.NotificationParented);
            // Use a Godot Callable to invoke our static method on the next frame.
            Callable.From(DeferredShow).CallDeferred();
        }
        catch
        {
            _showQueued = false;
        }
    }

    [HarmonyPatch(typeof(NCreature), "OnUnfocus")]
    [HarmonyPostfix]
    public static void AfterCreatureUnfocus(NCreature __instance)
    {
        if (__instance == null) return;
        _pendingHide = __instance;
        if (_hideQueued) return;
        _hideQueued = true;
        try
        {
            Callable.From(DeferredHide).CallDeferred();
        }
        catch
        {
            _hideQueued = false;
        }
    }

    // ── Deferred handlers (run outside Win32 message dispatch) ─────

    private static void DeferredShow()
    {
        var target = _pendingShow;
        _pendingShow = null;
        _showQueued = false;
        if (target == null) return;
        Safe.Run(() => ShowPanel(target));
    }

    private static void DeferredHide()
    {
        var target = _pendingHide;
        _pendingHide = null;
        _hideQueued = false;
        if (target == null) return;
        Safe.Run(() => HidePanel(target));
    }

    // ── Actual show / hide work — runs deferred, NEVER from postfix ─

    private static void ShowPanel(NCreature creatureNode)
    {
        if (creatureNode == null) return;
        if (!Godot.GodotObject.IsInstanceValid(creatureNode)) return;

        // Wrap every Godot binding call in try/catch — defensive against
        // any state inconsistency the bisect didn't surface.
        try
        {
            if (creatureNode.HasMeta(PanelMeta)) return;

            var entity = creatureNode.Entity;
            if (entity == null) return;
            if (entity.IsPlayer) return;
            if (entity.Monster == null) return;

            var panel = IntentStateMachinePanel.Create(entity);
            if (panel == null) return;

            // Add to the scene tree ROOT instead of the creature itself —
            // this way the panel doesn't follow creature destruction and
            // we never AddChild into a half-constructed combat node.
            var tree = Engine.GetMainLoop() as SceneTree;
            var root = tree?.Root;
            if (root == null) return;

            root.AddChild(panel);
            panel.ZIndex = 500;
            panel.GlobalPosition = creatureNode.GlobalPosition + new Vector2(20f, -200f);
            ClampPanelPosition(creatureNode, panel);

            creatureNode.SetMeta(PanelMeta, true);

            // Stash the panel reference on the creature node so we can find
            // it again on unfocus. Use a meta entry holding the panel name.
            creatureNode.SetMeta(PanelMeta + "_name", panel.Name);
        }
        catch (System.Exception ex)
        {
            Safe.Warn($"[IntentHover] ShowPanel failed: {ex.Message}");
        }
    }

    private static void HidePanel(NCreature creatureNode)
    {
        if (creatureNode == null) return;
        if (!Godot.GodotObject.IsInstanceValid(creatureNode)) return;

        try
        {
            if (!creatureNode.HasMeta(PanelMeta)) return;

            // Find the panel by the cached name on the scene root.
            var nameMeta = creatureNode.GetMeta(PanelMeta + "_name");
            var panelName = nameMeta.AsString();
            var tree = Engine.GetMainLoop() as SceneTree;
            var root = tree?.Root;
            if (root != null && !string.IsNullOrEmpty(panelName))
            {
                var existing = root.GetNodeOrNull(panelName);
                if (existing != null && Godot.GodotObject.IsInstanceValid(existing))
                {
                    existing.GetParent()?.RemoveChild(existing);
                    existing.QueueFree();
                }
            }

            creatureNode.RemoveMeta(PanelMeta);
            creatureNode.RemoveMeta(PanelMeta + "_name");
        }
        catch (System.Exception ex)
        {
            Safe.Warn($"[IntentHover] HidePanel failed: {ex.Message}");
        }
    }

    private static void ClampPanelPosition(NCreature creatureNode, Control panel)
    {
        try
        {
            if (!Godot.GodotObject.IsInstanceValid(creatureNode)) return;
            if (!Godot.GodotObject.IsInstanceValid(panel)) return;
            var viewport = panel.GetViewportRect().Size;
            var size = panel.Size;
            // Round 9 round 22: top-align the panel against the viewport (per
            // user request "意图状态机上移，其上边缘贴近顶栏"). The X tracks the
            // creature so the panel still appears next to the hovered enemy.
            float x = creatureNode.GlobalPosition.X + 20f;
            float y = 8f;
            if (x + size.X > viewport.X - 8f) x = viewport.X - size.X - 8f;
            if (x < 8f) x = 8f;
            if (y + size.Y > viewport.Y - 8f) y = viewport.Y - size.Y - 8f;
            if (y < 8f) y = 8f;
            panel.GlobalPosition = new Vector2(x, y);
        }
        catch { }
    }
}
