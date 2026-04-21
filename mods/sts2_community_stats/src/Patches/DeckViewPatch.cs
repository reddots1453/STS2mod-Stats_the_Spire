using CommunityStats.Api;
using CommunityStats.UI;
using CommunityStats.Util;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Screens;

namespace CommunityStats.Patches;

/// <summary>
/// Patches NDeckViewScreen to show community pick rate / win rate labels
/// on each card when viewing the deck during a run.
/// </summary>
[HarmonyPatch]
public static class DeckViewPatch
{
    /// <summary>
    /// Shared meta key used by all card-stats label patches (DeckView, CardUpgrade, CardReward).
    /// Using a single key ensures cross-patch cleanup works correctly.
    /// </summary>
    public const string StatsLabelMeta = "community_stats_label";

    // PRD §3.13: track live deck view screens for DataRefreshed re-render.
    private static readonly List<WeakReference<NDeckViewScreen>> _liveScreens = new();

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
                    AfterDisplayCards(screen);
                else
                    _liveScreens.RemoveAt(i);
            }
        });
    }

    private static void TrackScreen(NDeckViewScreen screen)
    {
        for (int i = _liveScreens.Count - 1; i >= 0; i--)
        {
            if (!_liveScreens[i].TryGetTarget(out var t) || !GodotObject.IsInstanceValid(t))
                _liveScreens.RemoveAt(i);
            else if (t == screen) return;
        }
        _liveScreens.Add(new WeakReference<NDeckViewScreen>(screen));
    }

    /// <summary>
    /// Removes all stats labels from a parent node immediately (RemoveChild + QueueFree).
    /// Shared across patches to ensure no label duplication.
    /// </summary>
    public static void RemoveExistingLabel(Node parent)
    {
        var toRemove = new List<Node>();
        foreach (var child in parent.GetChildren())
        {
            if (child is Label lbl && lbl.HasMeta(StatsLabelMeta))
                toRemove.Add(child);
        }
        foreach (var node in toRemove)
        {
            parent.RemoveChild(node);
            node.QueueFree();
        }
    }

    [HarmonyPatch(typeof(NDeckViewScreen), "DisplayCards")]
    [HarmonyPostfix]
    public static void AfterDisplayCards(NDeckViewScreen __instance)
    {
        TrackScreen(__instance);
        Safe.Run(() =>
        {
            // Access the _grid (NCardGrid) which contains NGridCardHolder children
            var grid = Traverse.Create(__instance).Field("_grid").GetValue<object>();
            if (grid == null) return;

            // Get CurrentlyDisplayedCardHolders property
            var holders = Traverse.Create(grid).Property("CurrentlyDisplayedCardHolders")
                .GetValue<System.Collections.IEnumerable>();
            if (holders == null) return;

            // Round 9 round 33: respect the CardLibraryStats toggle. When off,
            // strip any leftover labels and bail.
            bool enabled = CommunityStats.Config.ModConfig.Toggles.CardLibraryStats;

            foreach (var holderObj in holders)
            {
                if (holderObj is not NGridCardHolder holder) continue;
                if (!enabled) { RemoveExistingLabel(holder); continue; }

                var cardModel = holder.CardModel;
                var cardId = cardModel?.Id.Entry;
                if (cardId == null) continue;

                RemoveExistingLabel(holder);

                var stats = StatsProvider.Instance.GetCardStats(cardId);
                Label label;
                if (stats != null)
                    label = StatsLabel.ForCardStats(stats);
                else if (!StatsProvider.Instance.HasBundle)
                    label = StatsLabel.ForLoading();
                else
                    label = StatsLabel.ForUnavailable();

                label.Position = new Vector2(0, 200);
                label.Size = new Vector2(300, 24);
                label.SetMeta(StatsLabelMeta, true);
                holder.AddChild(label);
            }
        });
    }
}
