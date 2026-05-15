using System;
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
/// **4.18 rewrite — hitbox-jitter flicker fix.**
///
/// Creatures like the Defect-lineage "experiment" enemy have pulsating
/// hitboxes that fire rapid OnUnfocus → OnFocus pairs under a static mouse
/// pointer. The previous implementation (Round 9 round 2) tore the panel
/// down on every OnUnfocus and rebuilt it on every OnFocus, producing a
/// visible flicker whenever the cursor grazed such a creature. The
/// symptoms matched the 4.18 changelog item #1.
///
/// The fix, mirroring the 4.18 DLL implementation verbatim:
///
/// - Hide is *delayed* by <see cref="HideDelaySec"/> (120 ms). An
///   `_applyVersion` counter is bumped on every focus/unfocus; when the
///   delayed-hide timer fires it only proceeds if the token still matches
///   AND <see cref="_pendingShow"/> is false. A subsequent OnFocus within
///   the window increments the version (invalidating the pending hide)
///   and sets _pendingShow=true (double safety), so the panel persists.
/// - A single `_shownOn` reference tracks which creature currently owns a
///   panel; moving the cursor from one creature to another defers the old
///   creature's Hide while still scheduling the new Show immediately.
/// - `ForceMouseIgnoreRecursive` sweeps the panel subtree to ensure it
///   never captures mouse events — without this, the panel itself could
///   steal focus from the hovered creature and generate its own
///   focus/unfocus oscillation.
/// - All scene-tree mutations still run via `CallDeferred` so the Win32
///   WndProc crash from Round 9 stays fixed.
/// </summary>
[HarmonyPatch]
public static class IntentHoverPatch
{
    private const string PanelMeta = "sts_intent_state_panel";
    private const float HideDelaySec = 0.12f;

    private static NCreature? _pendingTarget;
    private static bool _pendingShow;
    private static bool _showQueued;
    private static ulong _applyVersion;
    private static NCreature? _shownOn;

    [HarmonyPatch(typeof(NCreature), "OnFocus")]
    [HarmonyPostfix]
    public static void AfterCreatureFocus(NCreature __instance)
    {
        if (!ModConfig.Toggles.IntentStateMachine) return;
        if (__instance == null) return;

        // Moving between creatures: defer the previous creature's Hide so
        // we don't tear down the old panel inside the native focus-event
        // dispatch (scene-tree mutation from WndProc is the Round 9 crash).
        if (_shownOn != null && _shownOn != __instance)
        {
            var previous = _shownOn;
            _shownOn = null;
            try
            {
                Callable.From(() => Safe.Run(() => HidePanel(previous))).CallDeferred();
            }
            catch { }
        }

        _pendingTarget = __instance;
        _pendingShow = true;
        _applyVersion++;
        ScheduleShow();
    }

    [HarmonyPatch(typeof(NCreature), "OnUnfocus")]
    [HarmonyPostfix]
    public static void AfterCreatureUnfocus(NCreature __instance)
    {
        if (__instance == null) return;
        _pendingTarget = __instance;
        _pendingShow = false;
        ScheduleDelayedHide(++_applyVersion, __instance);
    }

    // ── Deferred scheduling ─────────────────────────────────

    private static void ScheduleShow()
    {
        if (_showQueued) return;
        _showQueued = true;
        try
        {
            Callable.From(DeferredApplyImmediate).CallDeferred();
        }
        catch
        {
            _showQueued = false;
        }
    }

    /// <summary>
    /// Start a SceneTree timer; on expiry, hide the panel only if nothing
    /// has re-focused this creature in the meantime. Token comparison is
    /// the atomic guard — a newer focus has bumped <see cref="_applyVersion"/>
    /// past the captured value, and/or set <see cref="_pendingShow"/> to
    /// true, either of which cancels the hide.
    /// </summary>
    private static void ScheduleDelayedHide(ulong token, NCreature target)
    {
        try
        {
            var tree = Engine.GetMainLoop() as SceneTree;
            if (tree == null) return;
            var timer = tree.CreateTimer(HideDelaySec, true, false, false);
            timer.Timeout += () =>
            {
                if (token != _applyVersion) return;   // superseded by a later focus/unfocus
                if (_pendingShow) return;             // cursor came back onto the creature
                Safe.Run(() => HidePanel(target));
            };
        }
        catch { }
    }

    private static void DeferredApplyImmediate()
    {
        var target = _pendingTarget;
        var pendingShow = _pendingShow;
        _showQueued = false;
        if (target != null && pendingShow)
        {
            Safe.Run(() => ShowPanel(target));
        }
    }

    /// <summary>
    /// Force-remove ALL intent state machine panels from the scene root.
    /// Called at combat end and combat start to clean up panels that leaked
    /// because the creature node was removed while the mouse was still
    /// hovering (OnUnfocus never fires, HidePanel never runs).
    /// </summary>
    public static void ForceHideAll()
    {
        Safe.Run(() =>
        {
            var tree = Engine.GetMainLoop() as SceneTree;
            var root = tree?.Root;
            if (root == null) return;

            for (int i = root.GetChildCount() - 1; i >= 0; i--)
            {
                var child = root.GetChild(i);
                if (child is Control ctrl && ctrl.HasMeta(PanelMeta))
                {
                    root.RemoveChild(child);
                    child.QueueFree();
                }
            }

            // Clear hover state so a stale _shownOn doesn't block the next Show.
            _shownOn = null;
            _pendingTarget = null;
            _pendingShow = false;
            _showQueued = false;
            _applyVersion++;
        });
    }

    // ── Actual show / hide work — runs deferred, NEVER from postfix ─

    private static void ShowPanel(NCreature creatureNode)
    {
        if (creatureNode == null) return;
        if (!Godot.GodotObject.IsInstanceValid(creatureNode)) return;

        try
        {
            if (creatureNode.HasMeta(PanelMeta)) return;

            // Reject dead or removed creatures — the node may still be in the
            // tree during the dying animation but the Entity is gone/nulled.
            if (!creatureNode.IsInsideTree()) return;

            var entity = creatureNode.Entity;
            if (entity == null) return;
            if (entity.IsPlayer) return;
            if (entity.Monster == null) return;
            // Reject dead creatures — panel should never show for corpses.
            if (entity.IsDead) return;

            var panel = IntentStateMachinePanel.Create(entity);
            if (panel == null) return;

            var tree = Engine.GetMainLoop() as SceneTree;
            var root = tree?.Root;
            if (root == null) return;

            root.AddChild(panel);
            panel.SetMeta(PanelMeta, true);
            panel.ZIndex = 500;
            panel.GlobalPosition = creatureNode.GlobalPosition + new Vector2(20f, -200f);
            ClampPanelPosition(creatureNode, panel);
            ForceMouseIgnoreRecursive(panel);

            creatureNode.SetMeta(PanelMeta, true);
            creatureNode.SetMeta(PanelMeta + "_name", panel.Name);
            _shownOn = creatureNode;
        }
        catch (Exception ex)
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
            if (_shownOn == creatureNode) _shownOn = null;
        }
        catch (Exception ex)
        {
            Safe.Warn($"[IntentHover] HidePanel failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Disable mouse capture on the panel and every descendant so the
    /// panel itself can never receive focus events. Without this, a panel
    /// drawn at the cursor location would intercept the next OnFocus call
    /// meant for the creature underneath, producing an unfocus→refocus
    /// loop that competes with the hitbox-jitter flicker we're trying to
    /// fix.
    /// </summary>
    private static void ForceMouseIgnoreRecursive(Node node)
    {
        try
        {
            if (node is Control ctl)
            {
                ctl.MouseFilter = Control.MouseFilterEnum.Ignore;
            }
            foreach (var child in node.GetChildren())
            {
                ForceMouseIgnoreRecursive(child);
            }
        }
        catch { }
    }

    private static void ClampPanelPosition(NCreature creatureNode, Control panel)
    {
        try
        {
            if (!Godot.GodotObject.IsInstanceValid(creatureNode)) return;
            if (!Godot.GodotObject.IsInstanceValid(panel)) return;
            var viewport = panel.GetViewportRect().Size;
            var size = panel.Size;
            // X tracks the creature (set once at Show time — no further
            // recalculation since HasMeta short-circuits re-entry). Y is
            // top-anchored against the viewport so the panel sits under
            // the top bar regardless of the creature's vertical position.
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
