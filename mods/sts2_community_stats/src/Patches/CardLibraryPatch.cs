using System;
using CommunityStats.Api;
using CommunityStats.Collection;
using CommunityStats.Config;
using CommunityStats.UI;
using CommunityStats.Util;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens;

namespace CommunityStats.Patches;

/// <summary>
/// Compendium card library: when the player opens the inspect screen,
/// inject a small stats panel (round 5 PRD §3.2):
///   - 6 metric rows × 3 columns: metric name / mine / community
///   - "Mine" column = LocalCardStats from RunHistoryAnalyzer
///   - "Community" column = StatsProvider bundle (server-side data)
///   - Anchored to the screen's bottom-right so it doesn't conflict with
///     keyword hover-tip popups in the upper-right
///
/// Replaces the previous design that called four ForXxx() factory methods,
/// each of which appended its own win-rate row → win rate appeared 4 times.
/// </summary>
[HarmonyPatch]
public static class CardLibraryPatch
{
    private const string PanelName = "StatsTheSpireCardStats";

    // PRD §3.13: live inspect screens for DataRefreshed re-render.
    private static readonly List<WeakReference<NInspectCardScreen>> _liveScreens = new();

    public static void SubscribeRefresh()
    {
        StatsProvider.DataRefreshed += OnDataRefreshed;
    }

    private static void OnDataRefreshed()
    {
        Safe.Run(() =>
        {
            for (int i = _liveScreens.Count - 1; i >= 0; i--)
            {
                if (_liveScreens[i].TryGetTarget(out var screen) &&
                    GodotObject.IsInstanceValid(screen) && screen.IsInsideTree())
                    AfterUpdateCardDisplay(screen);
                else
                    _liveScreens.RemoveAt(i);
            }
        });
    }

    private static void TrackScreen(NInspectCardScreen screen)
    {
        for (int i = _liveScreens.Count - 1; i >= 0; i--)
        {
            if (!_liveScreens[i].TryGetTarget(out var t) || !GodotObject.IsInstanceValid(t))
                _liveScreens.RemoveAt(i);
            else if (t == screen) return;
        }
        _liveScreens.Add(new WeakReference<NInspectCardScreen>(screen));
    }

    private static readonly Color HeaderColor    = new("#EFC851");
    private static readonly Color CreamColor     = new("#FFF6E2");
    private static readonly Color GrayColor      = new(0.62f, 0.62f, 0.72f);
    private static readonly Color MineColor      = new(0.16f, 0.92f, 0.75f); // aqua
    private static readonly Color CommunityColor = new(0.95f, 0.78f, 0.30f); // gold
    private const int LabelSize  = 12;
    private const int HeaderSize = 11;

    [HarmonyPatch(typeof(NInspectCardScreen), "UpdateCardDisplay")]
    [HarmonyPostfix]
    public static void AfterUpdateCardDisplay(NInspectCardScreen __instance)
    {
        TrackScreen(__instance);
        if (!ModConfig.Toggles.CardLibraryStats)
        {
            // Round 9 round 33: actively remove any previously injected
            // panel so toggling off mid-screen takes effect immediately.
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

        // Find or create the panel anchored to screen bottom-right.
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
            panel.OffsetLeft   = -300f;
            panel.OffsetRight  = -20f;
            panel.OffsetTop    = -200f;
            panel.OffsetBottom = -20f;
            panel.GrowHorizontal = Control.GrowDirection.Begin;
            panel.GrowVertical   = Control.GrowDirection.Begin;
            panel.MouseFilter = Control.MouseFilterEnum.Ignore;
            screen.AddChild(panel);
        }

        // Clear any previous content.
        foreach (var c in panel.GetChildren()) c.QueueFree();

        var grid = new GridContainer { Columns = 3 };
        grid.AddThemeConstantOverride("h_separation", 14);
        grid.AddThemeConstantOverride("v_separation", 3);
        grid.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        panel.AddChild(grid);

        // Header row
        AddCell(grid, L.Get("card_lib.metric"),    GrayColor,      HeaderSize, false);
        AddCell(grid, L.Get("card_lib.mine"),      MineColor,      HeaderSize, true);
        AddCell(grid, L.Get("card_lib.community"), CommunityColor, HeaderSize, true);

        // Resolve both data sources.
        var mine     = RunHistoryAnalyzer.Instance.LocalCards;
        // Round 9 round 34: if the bundle hasn't been built yet (e.g. mod
        // just loaded and the user opened the library before the startup
        // LoadAllAsync finished), kick off a load and reschedule the inject
        // when it completes. The disk-cached career stats don't include
        // per-card data, so without this we'd render "0 samples" forever.
        if (mine.TotalRuns == 0) TriggerLazyLoad(screen);
        var mineRow  = mine.Get(cardId!);
        var community = StatsProvider.Instance.GetCardStats(cardId!);

        // Sample size row — Round 9 round 37: per-card sample = # of runs whose
        // final deck contained this card (mineRow?.RunsWith), NOT the total
        // run count of the bundle. This matches the user's "样本数 = 出现在
        // 最终牌组的局数" semantics.
        AddCell(grid, L.Get("card_lib.samples"), CreamColor, LabelSize, false);
        AddCell(grid, $"{mineRow?.RunsWith ?? 0}", MineColor, LabelSize, true);
        AddCell(grid, community != null ? $"{community.SampleSize}" : "—",
                community != null ? CommunityColor : GrayColor, LabelSize, true);

        // Pick / Win / Upgrade / Removal / Buy
        // Round 9 round 39: mine.{Upgrade,Removal,Buy}Rate now sourced from
        // PlayerMapPointHistoryEntry.{UpgradedCards, CardsRemoved,
        // BoughtColorless} + Shop-floor CardsGained — see ComputeLocalCardBundle.
        AddRatioRow(grid, "card_lib.pick_rate",
            mineRow != null && mineRow.Offered > 0 ? mineRow.PickRate : (float?)null,
            community?.PickRate);
        AddRatioRow(grid, "card_lib.win_rate",
            mineRow != null && mineRow.RunsWith > 0 ? mineRow.WinRate : (float?)null,
            community?.WinRate);
        AddRatioRow(grid, "card_lib.upgrade_rate",
            mineRow != null && mineRow.RunsWith > 0 ? mineRow.UpgradeRate : (float?)null,
            community?.UpgradeRate);
        AddRatioRow(grid, "card_lib.removal_rate",
            mineRow != null && mineRow.RunsWith > 0 ? mineRow.RemovalRate : (float?)null,
            community?.RemovalRate);
        AddRatioRow(grid, "card_lib.buy_rate",
            mineRow != null && mineRow.RunsWith > 0 ? mineRow.BuyRate : (float?)null,
            community?.ShopBuyRate);
    }

    private static void AddRatioRow(GridContainer grid, string labelKey, float? mine, float? community)
    {
        AddCell(grid, L.Get(labelKey), CreamColor, LabelSize, false);
        AddCell(grid, mine.HasValue ? $"{mine.Value * 100f:F1}%" : "—",
                mine.HasValue ? MineColor : GrayColor, LabelSize, true);
        AddCell(grid, community.HasValue ? $"{community.Value * 100f:F1}%" : "—",
                community.HasValue ? CommunityColor : GrayColor, LabelSize, true);
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
                // Re-render in case the user is still on the same card.
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
