using System;
using CommunityStats.Collection;
using CommunityStats.Config;
using CommunityStats.Util;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens;

namespace CommunityStats.Patches;

/// <summary>
/// Compendium card library: inject a personal stats panel showing the
/// player's own pick/win/upgrade/removal/buy rates from local run history.
/// Community column removed (local edition).
/// </summary>
[HarmonyPatch]
public static class CardLibraryPatch
{
    private const string PanelName = "StatsTheSpireCardStats";

    private static readonly Color HeaderColor = new("#EFC851");
    private static readonly Color CreamColor  = new("#FFF6E2");
    private static readonly Color GrayColor   = new(0.62f, 0.62f, 0.72f);
    private static readonly Color MineColor   = new(0.16f, 0.92f, 0.75f); // aqua
    private const int LabelSize  = 12;
    private const int HeaderSize = 11;

    [HarmonyPatch(typeof(NInspectCardScreen), "UpdateCardDisplay")]
    [HarmonyPostfix]
    public static void AfterUpdateCardDisplay(NInspectCardScreen __instance)
    {
        if (!ModConfig.Toggles.CardLibraryStats)
        {
            Safe.Run(() =>
            {
                var stale = __instance.GetNodeOrNull<PanelContainer>(PanelName);
                if (stale != null) stale.QueueFree();
            });
            return;
        }
        Safe.Run(() => InjectOrUpdate(__instance));
    }

    private static void InjectOrUpdate(NInspectCardScreen screen)
    {
        var cards = Traverse.Create(screen).Field("_cards").GetValue<System.Collections.IList>();
        var index = Traverse.Create(screen).Field("_index").GetValue<int>();
        if (cards == null || index < 0 || index >= cards.Count) return;

        var card = cards[index] as CardModel;
        if (card == null) return;

        string? cardId = null;
        try { cardId = card.Id.Entry; } catch { }
        if (string.IsNullOrEmpty(cardId)) return;

        // Find or create panel at screen bottom-right.
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
            panel.OffsetTop    = -200f;
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

        var mine = RunHistoryAnalyzer.Instance.LocalCards;
        if (mine.TotalRuns == 0) TriggerLazyLoad(screen);
        var mineRow = mine.Get(cardId!);

        // Sample size
        AddCell(grid, L.Get("card_lib.samples"), CreamColor, LabelSize, false);
        AddCell(grid, $"{mineRow?.RunsWith ?? 0}", MineColor, LabelSize, true);

        // Pick / Win / Upgrade / Removal / Buy
        AddSingleRow(grid, "card_lib.pick_rate",
            mineRow != null && mineRow.Offered > 0 ? mineRow.PickRate : (float?)null);
        AddSingleRow(grid, "card_lib.win_rate",
            mineRow != null && mineRow.RunsWith > 0 ? mineRow.WinRate : (float?)null);
        AddSingleRow(grid, "card_lib.upgrade_rate",
            mineRow != null && mineRow.RunsWith > 0 ? mineRow.UpgradeRate : (float?)null);
        AddSingleRow(grid, "card_lib.removal_rate",
            mineRow != null && mineRow.RunsWith > 0 ? mineRow.RemovalRate : (float?)null);
        AddSingleRow(grid, "card_lib.buy_rate",
            mineRow != null && mineRow.RunsWith > 0 ? mineRow.BuyRate : (float?)null);
    }

    private static void AddSingleRow(GridContainer grid, string labelKey, float? value)
    {
        AddCell(grid, L.Get(labelKey), CreamColor, LabelSize, false);
        AddCell(grid, value.HasValue ? $"{value.Value * 100f:F1}%" : "—",
                value.HasValue ? MineColor : GrayColor, LabelSize, true);
    }

    private static bool _loadInFlight;
    private static void TriggerLazyLoad(NInspectCardScreen screen)
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
            catch (Exception ex) { Safe.Warn($"CardLibrary lazy load failed: {ex.Message}"); }
            finally { _loadInFlight = false; }
        });
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
}
