using CommunityStats.Api;
using CommunityStats.Config;
using CommunityStats.UI;
using CommunityStats.Util;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Runs;

namespace CommunityStats.Patches;

/// <summary>
/// Patches NMapPoint.RefreshVisualsInstantly to overlay encounter danger stats
/// (death rate, avg damage) on combat map nodes.
/// </summary>
[HarmonyPatch]
public static class MapPointPatch
{
    private static readonly HashSet<MapPointType> CombatPointTypes = new()
    {
        MapPointType.Monster,
        MapPointType.Elite,
        MapPointType.Boss
    };

    [HarmonyPatch(typeof(NMapPoint), nameof(NMapPoint.RefreshVisualsInstantly))]
    [HarmonyPostfix]
    public static void AfterRefreshVisuals(NMapPoint __instance)
    {
        Safe.Run(() =>
        {
            var point = __instance.Point;
            if (point == null) return;
            if (!CombatPointTypes.Contains(point.PointType)) return;

            // Round 9 round 33: when MonsterDanger toggle is off, also strip
            // any overlay already attached on traveled nodes — previously the
            // toggle only suppressed *future* attachments, leaving old labels
            // visible until the player re-visited the screen.
            if (!ModConfig.Toggles.MonsterDanger)
            {
                MapPointOverlay.DetachFrom(__instance);
                return;
            }

            // Only show stats on nodes the player has already visited.
            if (__instance.State != MapPointState.Traveled)
            {
                MapPointOverlay.DetachFrom(__instance);
                return;
            }

            // Round 15 fix: previously this looked up the bundle by
            // `point.PointType.ToString().ToLowerInvariant()` (e.g. "monster")
            // — but bundle keys are real encounter IDs (SLIMES_NORMAL,
            // KNIGHTS_ELITE, KAISER_CRAB_BOSS, ...). The lookup never matched
            // and the overlay never showed. Resolve the actual encounter ID
            // for this traveled node from RunState.MapPointHistory, then look
            // it up by ID.
            var encounterId = ResolveEncounterIdForPoint(point);
            if (encounterId == null)
            {
                MapPointOverlay.DetachFrom(__instance);
                return;
            }

            var stats = StatsProvider.Instance.GetEncounterStats(encounterId);
            MapPointOverlay.AttachTo(__instance, stats);
        });
    }

    /// <summary>
    /// Round 15: derive the actual encounter ModelId for a traveled map
    /// point by indexing into <see cref="RunState.MapPointHistory"/>. The
    /// game's own <c>RunState.GetHistoryEntryFor</c> uses
    /// <c>history[actIndex][coord.row]</c>, so we mirror that. We try the
    /// current act first (covers the common map screen case); if the row
    /// doesn't fit, we fall back to scanning all acts for a Rooms entry on
    /// that row. Returns the first room's ModelId (combat / elite / boss
    /// rooms always have a single room).
    /// </summary>
    private static string? ResolveEncounterIdForPoint(MapPoint point)
    {
        try
        {
            var runState = RunManager.Instance?.DebugOnlyGetState();
            if (runState == null) return null;

            var hist = runState.MapPointHistory;
            if (hist == null || hist.Count == 0) return null;

            int row = point.coord.row;

            // Walk acts newest → oldest so re-visited rows from older acts
            // don't shadow the current act when both happen to share an index.
            for (int act = hist.Count - 1; act >= 0; act--)
            {
                var actHist = hist[act];
                if (actHist == null || row >= actHist.Count) continue;
                var entry = actHist[row];
                if (entry?.Rooms == null || entry.Rooms.Count == 0) continue;
                if (entry.MapPointType != point.PointType) continue;
                var room = entry.Rooms[0];
                var id = room.ModelId?.Entry;
                if (!string.IsNullOrEmpty(id)) return id;
            }
        }
        catch (System.Exception ex)
        {
            Safe.Warn($"[MapPointPatch] ResolveEncounterIdForPoint failed: {ex.Message}");
        }
        return null;
    }

    // ── Hover panels (PRD 3.8 unknown room + 3.16 shop prices) ─

    private const string HoverPanelMeta = "sts_hover_info_panel";

    [HarmonyPatch(typeof(NMapPoint), "OnFocus")]
    [HarmonyPostfix]
    public static void AfterOnFocus(NMapPoint __instance)
    {
        Safe.Run(() => ShowHoverPanel(__instance));
    }

    [HarmonyPatch(typeof(NMapPoint), "OnUnfocus")]
    [HarmonyPostfix]
    public static void AfterOnUnfocus(NMapPoint __instance)
    {
        Safe.Run(() => HideHoverPanel(__instance));
    }

    private static void ShowHoverPanel(NMapPoint mapPoint)
    {
        if (mapPoint.HasMeta(HoverPanelMeta)) return;

        var point = mapPoint.Point;
        if (point == null) return;

        InfoModPanel? panel = null;

        if (point.PointType == MapPointType.Unknown
            && mapPoint.State != MapPointState.Traveled
            && ModConfig.Toggles.UnknownRoomOdds)
        {
            panel = BuildUnknownPanel();
        }
        else if (point.PointType == MapPointType.Shop
            && mapPoint.State != MapPointState.Traveled
            && ModConfig.Toggles.ShopPrices)
        {
            var player = RunManager.Instance?.DebugOnlyGetState()?.Players?.FirstOrDefault();
            panel = ShopPricePanel.Create(player, GetShopRemovalsUsed());
        }

        if (panel == null) return;

        mapPoint.AddChild(panel);
        panel.ZIndex = 200;
        panel.GlobalPosition = mapPoint.GlobalPosition + new Vector2(40f, 0f);
        mapPoint.SetMeta(HoverPanelMeta, true);
    }

    private static InfoModPanel? BuildUnknownPanel()
    {
        var runState = RunManager.Instance?.DebugOnlyGetState();
        var odds = runState?.Odds?.UnknownMapPoint;
        if (odds == null) return null;
        return UnknownRoomPanel.Create(odds, runState);
    }

    private static int GetShopRemovalsUsed()
    {
        try
        {
            var runState = RunManager.Instance?.DebugOnlyGetState();
            var me = runState?.Players?.FirstOrDefault();
            if (me == null) return 0;
            var used = Traverse.Create(me).Field("ExtraFields")
                .Field("CardShopRemovalsUsed").GetValue<int>();
            return used;
        }
        catch { return 0; }
    }

    private static void HideHoverPanel(NMapPoint mapPoint)
    {
        if (!mapPoint.HasMeta(HoverPanelMeta)) return;

        foreach (var child in mapPoint.GetChildren())
        {
            if (child is InfoModPanel)
            {
                child.QueueFree();
                break;
            }
        }
        mapPoint.RemoveMeta(HoverPanelMeta);
    }

}
