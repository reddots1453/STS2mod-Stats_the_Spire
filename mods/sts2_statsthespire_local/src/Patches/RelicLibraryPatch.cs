using System;
using CommunityStats.Collection;
using CommunityStats.Config;
using CommunityStats.Util;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens.InspectScreens;

namespace CommunityStats.Patches;

/// <summary>
/// Compendium relic library: inject a personal stats panel showing the
/// player's own win rate and delta from local run history.
/// Community column removed (local edition).
/// </summary>
[HarmonyPatch]
public static class RelicLibraryPatch
{
    private const string PanelName = "StatsTheSpireRelicStats";

    private static readonly Color HeaderColor = new("#EFC851");
    private static readonly Color CreamColor  = new("#FFF6E2");
    private static readonly Color GrayColor   = new(0.62f, 0.62f, 0.72f);
    private static readonly Color MineColor   = new(0.16f, 0.92f, 0.75f); // aqua
    private static readonly Color GreenColor  = new(0.30f, 0.85f, 0.40f);
    private static readonly Color RedColor    = new(0.90f, 0.30f, 0.30f);
    private const int LabelSize  = 12;
    private const int HeaderSize = 11;

    [HarmonyPatch(typeof(NInspectRelicScreen), "UpdateRelicDisplay")]
    [HarmonyPostfix]
    public static void AfterUpdateRelicDisplay(NInspectRelicScreen __instance)
    {
        if (!ModConfig.Toggles.RelicStats) return;
        Safe.Run(() => InjectOrUpdate(__instance));
    }

    private static void InjectOrUpdate(NInspectRelicScreen screen)
    {
        var relics = Traverse.Create(screen).Field("_relics").GetValue<System.Collections.IList>();
        var index = Traverse.Create(screen).Field("_index").GetValue<int>();
        if (relics == null || index < 0 || index >= relics.Count) return;

        var relic = relics[index] as RelicModel;
        if (relic == null) return;

        string? relicId = null;
        try { relicId = relic.Id.Entry; } catch { }
        if (string.IsNullOrEmpty(relicId)) return;

        var panel = screen.GetNodeOrNull<PanelContainer>(PanelName);
        if (panel == null)
        {
            panel = new PanelContainer { Name = PanelName };
            var style = new StyleBoxFlat
            {
                BgColor = new Color(0.05f, 0.06f, 0.10f, 0.92f),
                BorderColor = new Color(0.3f, 0.4f, 0.6f, 0.5f),
                BorderWidthBottom = 1, BorderWidthTop = 1,
                BorderWidthLeft = 1, BorderWidthRight = 1,
                CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6,
                CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
                ContentMarginLeft = 12, ContentMarginRight = 12,
                ContentMarginTop = 8, ContentMarginBottom = 8,
            };
            panel.AddThemeStyleboxOverride("panel", style);
            panel.ZIndex = 100;
            panel.AnchorLeft   = 1.0f;
            panel.AnchorRight  = 1.0f;
            panel.AnchorTop    = 1.0f;
            panel.AnchorBottom = 1.0f;
            panel.OffsetLeft   = -220f;
            panel.OffsetRight  = -20f;
            panel.OffsetTop    = -140f;
            panel.OffsetBottom = -20f;
            panel.GrowHorizontal = Control.GrowDirection.Begin;
            panel.GrowVertical   = Control.GrowDirection.Begin;
            panel.MouseFilter = Control.MouseFilterEnum.Ignore;
            screen.AddChild(panel);
        }

        foreach (var c in panel.GetChildren()) c.QueueFree();

        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 14);
        grid.AddThemeConstantOverride("v_separation", 3);
        grid.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        panel.AddChild(grid);

        // Header row
        AddCell(grid, L.Get("card_lib.metric"), GrayColor, HeaderSize, false);
        AddCell(grid, L.Get("card_lib.mine"), MineColor, HeaderSize, true);

        var mine = RunHistoryAnalyzer.Instance.LocalRelics;
        if (mine.TotalRuns == 0) TriggerLazyLoad(screen);
        var mineRow = mine.Get(relicId!);

        // Sample size
        AddCell(grid, L.Get("card_lib.samples"), CreamColor, LabelSize, false);
        AddCell(grid, $"{mineRow?.RunsWith ?? 0}", MineColor, LabelSize, true);

        // Win rate
        AddCell(grid, L.Get("card_lib.win_rate"), CreamColor, LabelSize, false);
        AddCell(grid, mineRow != null && mineRow.RunsWith > 0
                ? $"{mineRow.WinRate * 100f:F1}%" : "—",
                mineRow != null ? MineColor : GrayColor, LabelSize, true);

        // Win rate delta (vs. career average)
        AddCell(grid, L.Get("career.delta"), CreamColor, LabelSize, false);
        AddDeltaCell(grid, mineRow != null && mineRow.RunsWith > 0
                ? mineRow.WinRate - mine.AverageWinRate : (float?)null);
    }

    private static void AddDeltaCell(GridContainer grid, float? delta)
    {
        if (!delta.HasValue)
        {
            AddCell(grid, "—", GrayColor, LabelSize, true);
            return;
        }
        var d = delta.Value * 100f;
        var sign = d >= 0 ? "+" : "";
        var color = MathF.Abs(d) < 1f ? CreamColor : (d >= 0 ? GreenColor : RedColor);
        AddCell(grid, $"{sign}{d:F1}%", color, LabelSize, true);
    }

    private static void AddCell(GridContainer grid, string text, Color color, int fontSize, bool rightAlign)
    {
        var lbl = new Label { Text = text };
        lbl.AddThemeColorOverride("font_color", color);
        lbl.AddThemeFontSizeOverride("font_size", fontSize);
        if (rightAlign)
        {
            lbl.HorizontalAlignment = HorizontalAlignment.Right;
            lbl.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        }
        grid.AddChild(lbl);
    }

    private static bool _loadInFlight;
    private static void TriggerLazyLoad(NInspectRelicScreen screen)
    {
        if (_loadInFlight) return;
        _loadInFlight = true;
        Safe.RunAsync(async () =>
        {
            try
            {
                await RunHistoryAnalyzer.Instance.LoadAllAsync(null, force: true);
                Safe.Run(() => InjectOrUpdate(screen));
            }
            catch (Exception ex) { Safe.Warn($"RelicLibrary lazy load failed: {ex.Message}"); }
            finally { _loadInFlight = false; }
        });
    }
}
