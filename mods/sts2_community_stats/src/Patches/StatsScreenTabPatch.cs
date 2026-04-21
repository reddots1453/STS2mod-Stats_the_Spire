using System;
using CommunityStats.UI;
using CommunityStats.Util;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;
using MegaCrit.Sts2.Core.Nodes.Screens.StatsScreen;

namespace CommunityStats.Patches;

/// <summary>
/// Round 9 round 35 — Plan A: inject a third tab "生涯统计" into the
/// 百科大全 → 角色数据 screen, replacing the old approach where
/// CareerStatsPatch wedged a section between the GridContainer and the
/// CharacterStats grid (which overlapped because the parent layout was
/// not a flow container).
///
/// Strategy:
///   1. Postfix NStatsScreen._Ready: clone the existing 统计 tab button
///      and add it as a sibling in the tab manager. Build a ScrollContainer
///      sibling of _statsGrid that hosts CareerStatsSection. Wire the new
///      tab's Released signal to a callback that hides _statsGrid and
///      shows the career container.
///   2. Postfix NStatsScreen.OpenStatsMenu: hide the career container so
///      switching back to the 统计 tab doesn't leave it overlaying.
///
/// Idempotency: NStatsScreen is a cached PackedScene singleton, so its
/// _Ready only fires once per game session. We still guard with a Meta
/// flag in case the cache is rebuilt.
/// </summary>
[HarmonyPatch]
public static class StatsScreenTabPatch
{
    private const string InjectedFlagMeta = "sts_career_tab_injected";
    private const string CareerContainerName = "StsCareerTabContent";
    private const string CareerTabName = "StsCareerTabButton";

    [HarmonyPatch(typeof(NStatsScreen), "_Ready")]
    [HarmonyPostfix]
    public static void AfterReady(NStatsScreen __instance)
    {
        Safe.Run(() => InjectCareerTab(__instance));
    }

    [HarmonyPatch(typeof(NStatsScreen), "OpenStatsMenu")]
    [HarmonyPostfix]
    public static void AfterOpenStatsMenu(NStatsScreen __instance)
    {
        Safe.Run(() =>
        {
            var container = __instance.GetNodeOrNull<Control>(CareerContainerName);
            if (container != null) container.Visible = false;
        });
    }

    private static void InjectCareerTab(NStatsScreen screen)
    {
        if (screen.HasMeta(InjectedFlagMeta))
        {
            Safe.Info("[StatsScreenTabPatch] already injected, skipping");
            return;
        }

        // Read private fields via reflection.
        var statsTab     = Traverse.Create(screen).Field("_statsTab").GetValue<NSettingsTab>();
        var statsGrid    = Traverse.Create(screen).Field("_statsGrid").GetValue<NGeneralStatsGrid>();
        var tabManager   = Traverse.Create(screen).Field("_statsTabManager").GetValue<NStatsTabManager>();
        if (statsTab == null || statsGrid == null || tabManager == null)
        {
            Safe.Warn("[StatsScreenTabPatch] missing required fields, aborting");
            return;
        }

        var tabContainer = Traverse.Create(tabManager).Field("_tabContainer").GetValue<Control>();
        if (tabContainer == null)
        {
            Safe.Warn("[StatsScreenTabPatch] _tabContainer null, aborting");
            return;
        }

        // ── 1. Clone the 统计 tab button into a new tab ───────
        NSettingsTab? newTab = null;
        try
        {
            newTab = (NSettingsTab)statsTab.Duplicate(
                (int)(Node.DuplicateFlags.Signals
                    | Node.DuplicateFlags.Groups
                    | Node.DuplicateFlags.Scripts
                    | Node.DuplicateFlags.UseInstantiation));
        }
        catch (Exception ex)
        {
            Safe.Warn($"[StatsScreenTabPatch] tab Duplicate failed: {ex.Message}");
            return;
        }
        if (newTab == null) return;

        newTab.Name = CareerTabName;
        tabContainer.AddChild(newTab);
        // Set label after AddChild so the cloned _Ready has wired up _label.
        try { newTab.SetLabel(Config.L.Get("career.title")); }
        catch (Exception ex) { Safe.Warn($"[StatsScreenTabPatch] SetLabel failed: {ex.Message}"); }

        // Add to NStatsTabManager._tabs so L/R trigger navigation works.
        try
        {
            var tabsList = Traverse.Create(tabManager).Field("_tabs")
                .GetValue<System.Collections.Generic.List<NSettingsTab>>();
            tabsList?.Add(newTab);
        }
        catch (Exception ex)
        {
            Safe.Warn($"[StatsScreenTabPatch] could not append to _tabs: {ex.Message}");
        }

        // ── 2. Build the content container as a sibling of _statsGrid ──
        var gridParent = statsGrid.GetParent() as Control;
        if (gridParent == null)
        {
            Safe.Warn("[StatsScreenTabPatch] statsGrid has no Control parent");
            return;
        }

        // Round 9 round 40: previously copied `_statsGrid`'s anchors verbatim,
        // which left the ScrollContainer unbounded — its child VBox grew with
        // content and the ScrollContainer never actually scrolled, blocking
        // the back button visually. Now we anchor the scroll explicitly to
        // the screen with margins that leave room for tabs (top) and the
        // back button (top-left + bottom). MouseFilter = Pass lets clicks on
        // empty regions reach the back button beneath us.
        var careerScroll = new ScrollContainer
        {
            Name = CareerContainerName,
            Visible = false,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
            FollowFocus = true,
            MouseFilter = Control.MouseFilterEnum.Pass,
            // Anchor to full parent, then bound with screen-relative offsets:
            //   top  140 = leave room for the tab bar
            //   bot  -60 = leave room for any bottom HUD
            //   l/r  ±200 = centered narrow column so back button at top-left
            //               stays uncovered AND content reads comfortably
            AnchorLeft = 0f,
            AnchorRight = 1f,
            AnchorTop = 0f,
            AnchorBottom = 1f,
            OffsetLeft = 200f,
            OffsetRight = -200f,
            OffsetTop = 140f,
            OffsetBottom = -60f,
        };
        gridParent.AddChild(careerScroll);

        var section = CareerStatsSection.Create(characterFilter: null);
        section.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        section.MouseFilter = Control.MouseFilterEnum.Pass;
        careerScroll.AddChild(section);

        // ── 3. Wire the new tab's Released signal ─────────────
        // Capture references for the lambda.
        var captureScreen = screen;
        var captureTabMgr = tabManager;
        var captureNewTab = newTab;
        var captureGrid = statsGrid;
        var captureScroll = careerScroll;

        newTab.Connect(NClickableControl.SignalName.Released, Callable.From<NClickableControl>(_ =>
        {
            Safe.Run(() =>
            {
                captureGrid.Visible = false;
                captureScroll.Visible = true;
                // Update tab highlight via the manager so SwitchToTab handles
                // Select/Deselect bookkeeping for us.
                Traverse.Create(captureTabMgr).Method("SwitchToTab", captureNewTab).GetValue();
            });
        }));

        screen.SetMeta(InjectedFlagMeta, true);
        Safe.Info("[StatsScreenTabPatch] career tab injected successfully");
    }
}
