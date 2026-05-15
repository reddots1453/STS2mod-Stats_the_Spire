using CommunityStats.Collection;
using CommunityStats.Util;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;

namespace CommunityStats.Patches;

/// <summary>
/// Patches NDeckCardSelectScreen to record removed cards.
/// Community stat labels removed (local edition).
/// </summary>
[HarmonyPatch]
public static class CardRemovalPatch
{
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
