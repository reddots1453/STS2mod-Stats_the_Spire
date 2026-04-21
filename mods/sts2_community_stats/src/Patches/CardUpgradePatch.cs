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
/// Patches NDeckUpgradeSelectScreen to:
/// 1. Display upgrade rate labels on each card
/// 2. Record upgraded cards
/// </summary>
[HarmonyPatch]
public static class CardUpgradePatch
{
    // PRD §3.13: live upgrade screens for DataRefreshed re-render.
    private static readonly List<WeakReference<NDeckUpgradeSelectScreen>> _liveScreens = new();

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

    private static void TrackScreen(NDeckUpgradeSelectScreen screen)
    {
        for (int i = _liveScreens.Count - 1; i >= 0; i--)
        {
            if (!_liveScreens[i].TryGetTarget(out var t) || !GodotObject.IsInstanceValid(t))
                _liveScreens.RemoveAt(i);
            else if (t == screen) return;
        }
        _liveScreens.Add(new WeakReference<NDeckUpgradeSelectScreen>(screen));
    }

    // ── Display upgrade rate when screen shows cards ────────

    [HarmonyPatch(typeof(NDeckUpgradeSelectScreen), nameof(NDeckUpgradeSelectScreen._Ready))]
    [HarmonyPostfix]
    public static void AfterScreenReady(NDeckUpgradeSelectScreen __instance)
    {
        TrackScreen(__instance);
        Safe.Run(() =>
        {
            // Access card grid to attach labels
            var grid = Traverse.Create(__instance).Field("_grid").GetValue<object>();
            if (grid == null)
            {
                // Fallback: try field names that might exist
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
                    label = StatsLabel.ForUpgradeRate(stats);
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

    // ── Record upgrade on confirm ───────────────────────────

    [HarmonyPatch(typeof(NDeckUpgradeSelectScreen), "ConfirmSelection")]
    [HarmonyPostfix]
    public static void AfterUpgradeConfirm(NDeckUpgradeSelectScreen __instance)
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
                        RunDataCollector.RecordCardUpgrade(entry, "campfire");
                    }
                }
            }
        });
    }
}
