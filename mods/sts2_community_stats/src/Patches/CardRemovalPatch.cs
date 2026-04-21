using CommunityStats.Api;
using CommunityStats.Collection;
using CommunityStats.UI;
using CommunityStats.Util;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;

namespace CommunityStats.Patches;

/// <summary>
/// Patches NDeckCardSelectScreen to:
/// 1. Display removal rate labels on each card
/// 2. Record removed cards
/// </summary>
[HarmonyPatch]
public static class CardRemovalPatch
{
    // PRD §3.13: live removal screens for DataRefreshed re-render.
    private static readonly List<WeakReference<NDeckCardSelectScreen>> _liveScreens = new();

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
                    AfterScreenReady(screen);
                else
                    _liveScreens.RemoveAt(i);
            }
        });
    }

    private static void TrackScreen(NDeckCardSelectScreen screen)
    {
        for (int i = _liveScreens.Count - 1; i >= 0; i--)
        {
            if (!_liveScreens[i].TryGetTarget(out var t) || !GodotObject.IsInstanceValid(t))
                _liveScreens.RemoveAt(i);
            else if (t == screen) return;
        }
        _liveScreens.Add(new WeakReference<NDeckCardSelectScreen>(screen));
    }

    // ── Display removal rate when screen shows cards ────────

    [HarmonyPatch(typeof(NDeckCardSelectScreen), nameof(NDeckCardSelectScreen._Ready))]
    [HarmonyPostfix]
    public static void AfterScreenReady(NDeckCardSelectScreen __instance)
    {
        TrackScreen(__instance);
        Safe.Run(() =>
        {
            // Access card grid to attach labels
            var grid = Traverse.Create(__instance).Field("_grid").GetValue<object>();
            if (grid == null)
            {
                grid = Traverse.Create(__instance).Field("_cardGrid").GetValue<object>();
            }
            if (grid == null) return;

            var holders = Traverse.Create(grid).Property("CurrentlyDisplayedCardHolders")
                .GetValue<System.Collections.IEnumerable>();
            if (holders == null) return;

            foreach (var holderObj in holders)
            {
                if (holderObj is not NGridCardHolder holder) continue;

                var cardModel = holder.CardModel;
                var cardId = cardModel?.Id.Entry;
                if (cardId == null) continue;

                DeckViewPatch.RemoveExistingLabel(holder);

                var stats = StatsProvider.Instance.GetCardStats(cardId);
                Label label;
                if (stats != null)
                    label = StatsLabel.ForRemovalRate(stats);
                else if (!StatsProvider.Instance.HasBundle)
                    label = StatsLabel.ForLoading();
                else
                    label = StatsLabel.ForUnavailable();

                label.Position = new Vector2(0, 200);
                label.Size = new Vector2(300, 24);
                label.SetMeta(DeckViewPatch.StatsLabelMeta, true);
                holder.AddChild(label);
            }
        });
    }

    // ── Record removal on confirm ───────────────────────────

    [HarmonyPatch(typeof(NDeckCardSelectScreen), "ConfirmSelection")]
    [HarmonyPostfix]
    public static void AfterConfirmSelection(NDeckCardSelectScreen __instance)
    {
        Safe.Run(() =>
        {
            var selectedCards = Traverse.Create(__instance).Field("_selectedCards").GetValue<object>();
            if (selectedCards == null) return;

            if (selectedCards is System.Collections.IEnumerable enumerable)
            {
                foreach (var card in enumerable)
                {
                    var id = Traverse.Create(card).Property("Id").GetValue<object>();
                    var entry = Traverse.Create(id).Property("Entry").GetValue<string>();

                    if (entry != null)
                    {
                        RunDataCollector.RecordCardRemoval(entry, "shop", RunDataCollector.CurrentFloor);
                    }
                }
            }
        });
    }

}
