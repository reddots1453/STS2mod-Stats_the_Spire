using CommunityStats.Config;
using CommunityStats.UI;
using CommunityStats.Util;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.RunHistoryScreen;
using MegaCrit.Sts2.Core.Runs;

namespace CommunityStats.Patches;

/// <summary>
/// Round 9 round 51 (rev 2): button + root-parented overlay.
///
/// The previous attempt parented the overlay to NRunHistory's Control which
/// turned out to share input/z-order state with the screen's tween system,
/// trapping input after close. ContributionPanel is parented to GetTree().Root
/// — to layer cleanly with it (and to avoid any NRunHistory internals), our
/// overlay is now also parented to root.
///
/// On 查看贡献图表 click, RunHistoryStatsSection invokes a callback that
/// frees this overlay, so the contribution panel pops on top of an empty
/// background instead of fighting our overlay for z-order.
/// </summary>
[HarmonyPatch]
public static class RunHistoryPatch
{
    private const string ButtonMeta  = "sts_run_hist_button";
    private const string PopupName   = "ModRunHistoryStatsPopup";

    [HarmonyPatch(typeof(NRunHistory), "SelectPlayer")]
    [HarmonyPostfix]
    public static void AfterSelectPlayer(NRunHistory __instance)
    {
        // Round 9 round 51 (rev 3): free any leftover popup before re-injecting
        // the button. The overlay is parented to GetTree().Root so it survives
        // across scene transitions — if the user opened it once and navigated
        // away without closing, the dim layer was still blocking input on
        // the next visit. SelectPlayer fires on every screen entry and on
        // player switches, so freeing here is the right cleanup point.
        Safe.Run(CloseOpenPopup);
        Safe.Run(() => InjectButton(__instance));
    }

    // Round 9 round 51 (rev 4): removed the _ExitTree postfix — NRunHistory
    // does not override _ExitTree, so the Harmony patch attribute couldn't
    // resolve the method and broke ALL patches in this class (including
    // SelectPlayer), which is why the button stopped appearing.
    // The CloseOpenPopup at the start of AfterSelectPlayer is enough cleanup:
    // any leftover overlay from a previous visit gets freed when the user
    // re-enters the run history screen.

    private static void InjectButton(NRunHistory screen)
    {
        RunHistory? history;
        Control? screenContents;
        try
        {
            history = Traverse.Create(screen).Field("_history").GetValue<RunHistory>();
            screenContents = Traverse.Create(screen).Field("_screenContents").GetValue<Control>();
        }
        catch (System.Exception ex)
        {
            Safe.Warn($"RunHistoryPatch: failed to read NRunHistory fields: {ex.Message}");
            return;
        }
        if (history == null || screenContents == null) return;

        // Diagnostic: dump the root child list so we can see exactly what's
        // alive when the user enters the run history screen.
        try
        {
            var tree = (Godot.Engine.GetMainLoop() as SceneTree);
            var root = tree?.Root;
            if (root != null)
            {
                int n = root.GetChildCount();
                Safe.Info($"[RunHistoryPatch] InjectButton: root has {n} children");
                for (int i = 0; i < n; i++)
                {
                    var c = root.GetChild(i);
                    Safe.Info($"  root[{i}] = {c.GetType().Name} \"{c.Name}\" Visible={(c is Control ctrl ? ctrl.Visible.ToString() : "n/a")}");
                }
            }
            Safe.Info($"[RunHistoryPatch] _screenContents={screenContents.GetType().Name} pos={screenContents.Position} size={screenContents.Size}");
        }
        catch (System.Exception ex) { Safe.Warn($"diagnostic dump failed: {ex.Message}"); }

        // Round 9 round 51 (rev 5): button now lives on `screen` (NRunHistory)
        // not `screenContents` (a MarginContainer). MarginContainer is a
        // LAYOUT container — it ignores child anchors and stretches its lone
        // child to fill the entire content area. That's why the previous
        // version made the button balloon to ~1920×1080 with its default
        // background filling the screen as a dim "overlay".
        var button = FindChildByMeta(screen, ButtonMeta) as Button;
        if (button == null)
        {
            button = new Button
            {
                Name = "ModRunHistoryStatsButton",
                Text = "📊  " + L.Get("run_hist_btn"),
                MouseFilter = Control.MouseFilterEnum.Stop,
                // Round 9 round 51 (rev 3): no keyboard focus — prevent any
                // accidental Space/Enter activation while navigating the
                // surrounding native widgets.
                FocusMode = Control.FocusModeEnum.None,
            };
            button.SetMeta(ButtonMeta, true);
            button.AddThemeFontSizeOverride("font_size", 22);

            // Round 9 round 52: moved from top-right to below the player
            // icon container (top-left area). Anchors top-left with fixed
            // offsets that match the layout of the native player icon row.
            button.AnchorLeft   = 0.0f;
            button.AnchorRight  = 0.0f;
            button.AnchorTop    = 0.0f;
            button.AnchorBottom = 0.0f;
            button.OffsetLeft   = 30f;
            button.OffsetRight  = 290f;
            button.OffsetTop    = 210f;
            button.OffsetBottom = 260f;

            screen.AddChild(button);
        }

        // Drop any previous Pressed handler so we don't stack across SelectPlayer.
        try
        {
            var connections = button.GetSignalConnectionList(Button.SignalName.Pressed);
            foreach (var conn in connections)
            {
                var callable = conn["callable"].AsCallable();
                button.Disconnect(Button.SignalName.Pressed, callable);
            }
        }
        catch { }

        var capturedHistory = history;
        button.Pressed += () => Safe.Run(() => OpenPopup(capturedHistory));
    }

    /// <summary>
    /// Free any open run-history-stats popup. Called by both the popup's own
    /// close handlers and by RunHistoryStatsSection before it opens the
    /// contribution panel (so the contribution panel pops onto a clean
    /// background instead of fighting for z-order with us).
    /// </summary>
    public static void CloseOpenPopup()
    {
        var tree = (Godot.Engine.GetMainLoop() as SceneTree);
        var root = tree?.Root;
        if (root == null) return;
        int freed = 0;
        for (int i = root.GetChildCount() - 1; i >= 0; i--)
        {
            var c = root.GetChild(i);
            // StringName / string equality is unreliable across Godot
            // marshalling — compare via ToString() instead.
            if (c.Name.ToString() == PopupName)
            {
                if (c is Control ctrl) ctrl.Visible = false;
                root.RemoveChild(c);
                c.QueueFree();
                freed++;
            }
        }
        if (freed > 0) Safe.Info($"[RunHistoryPatch] CloseOpenPopup freed {freed} overlay(s)");
    }

    private static void OpenPopup(RunHistory history)
    {
        Safe.Info("[RunHistoryPatch] OpenPopup called (button click)");
        var tree = (Godot.Engine.GetMainLoop() as SceneTree);
        var root = tree?.Root;
        if (root == null) return;

        // Free any previous popup so we never stack two.
        CloseOpenPopup();

        var viewport = root.GetVisibleRect().Size;
        int popupW = (int)System.MathF.Min(1000f, viewport.X * 0.75f);
        int popupH = (int)System.MathF.Min(780f, viewport.Y * 0.88f);
        if (popupW < 520) popupW = 520;
        if (popupH < 400) popupH = 400;

        // Top-level Control overlay parented to root. Must explicitly fill
        // the viewport via anchors since root has no layout enforcement.
        var overlay = new Control
        {
            Name = PopupName,
            MouseFilter = Control.MouseFilterEnum.Stop,
            FocusMode = Control.FocusModeEnum.All,
        };
        overlay.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        // Dim background — clicking it closes the popup.
        var dim = new ColorRect
        {
            Name = "Dim",
            Color = new Color(0f, 0f, 0f, 0.55f),
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        dim.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        overlay.AddChild(dim);
        dim.GuiInput += ev =>
        {
            if (ev is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
            {
                CloseOpenPopup();
            }
        };

        // Centered panel.
        var panel = new PanelContainer
        {
            Name = "PopupPanel",
            CustomMinimumSize = new Vector2(popupW, popupH),
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        var bgStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.06f, 0.07f, 0.11f, 0.98f),
            BorderColor = new Color(0.55f, 0.70f, 0.95f, 0.7f),
            BorderWidthBottom = 2, BorderWidthTop = 2,
            BorderWidthLeft = 2, BorderWidthRight = 2,
            CornerRadiusBottomLeft = 10, CornerRadiusBottomRight = 10,
            CornerRadiusTopLeft = 10, CornerRadiusTopRight = 10,
            ContentMarginLeft = 18, ContentMarginRight = 18,
            ContentMarginTop = 14, ContentMarginBottom = 14,
        };
        panel.AddThemeStyleboxOverride("panel", bgStyle);
        panel.AnchorLeft = 0.5f; panel.AnchorRight = 0.5f;
        panel.AnchorTop = 0.5f; panel.AnchorBottom = 0.5f;
        panel.OffsetLeft = -popupW / 2f;
        panel.OffsetRight = popupW / 2f;
        panel.OffsetTop = -popupH / 2f;
        panel.OffsetBottom = popupH / 2f;
        overlay.AddChild(panel);

        var body = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        body.AddThemeConstantOverride("separation", 8);
        panel.AddChild(body);

        // Title bar with close button.
        var titleRow = new HBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        titleRow.AddThemeConstantOverride("separation", 8);
        var titleLbl = new Label { Text = L.Get("run_hist_popup_title") };
        titleLbl.AddThemeFontSizeOverride("font_size", 30);
        titleLbl.AddThemeColorOverride("font_color", new Color("#EFC851"));
        titleLbl.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        titleRow.AddChild(titleLbl);

        var closeBtn = new Button
        {
            Text = "✕",
            CustomMinimumSize = new Vector2(40, 40),
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        closeBtn.AddThemeFontSizeOverride("font_size", 22);
        closeBtn.Pressed += () => CloseOpenPopup();
        titleRow.AddChild(closeBtn);
        body.AddChild(titleRow);

        // Scrollable content area.
        var scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Pass,
        };
        body.AddChild(scroll);

        var section = RunHistoryStatsSection.Create(history);
        section.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        section.SizeFlagsVertical = Control.SizeFlags.ShrinkBegin;
        scroll.AddChild(section);

        // Esc key handling: process input on the overlay itself.
        overlay.GuiInput += ev =>
        {
            if (ev is InputEventKey k && k.Pressed && k.Keycode == Key.Escape)
            {
                CloseOpenPopup();
                overlay.AcceptEvent();
            }
        };

        root.AddChild(overlay);
    }

    private static Node? FindChildByMeta(Node parent, string meta)
    {
        for (int i = 0; i < parent.GetChildCount(); i++)
        {
            var c = parent.GetChild(i);
            if (c.HasMeta(meta)) return c;
        }
        return null;
    }
}
