using CommunityStats.Collection;
using CommunityStats.Util;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Events;

namespace CommunityStats.Patches;

/// <summary>
/// Patches NEventOptionButton to record event choices.
/// Community stat labels removed (local edition).
/// </summary>
[HarmonyPatch]
public static class EventOptionPatch
{
    [HarmonyPatch(typeof(NEventOptionButton), "OnRelease")]
    [HarmonyPostfix]
    public static void AfterEventOptionSelected(NEventOptionButton __instance)
    {
        Safe.Run(() =>
        {
            var eventModel = __instance.Event;
            var option = __instance.Option;
            if (eventModel == null || option == null) return;

            var eventId = eventModel.Id.Entry;
            var index = Traverse.Create(__instance).Property("Index").GetValue<int>();
            var totalOptions = eventModel.CurrentOptions?.Count ?? 0;

            RunDataCollector.RecordEventChoice(eventId, index, totalOptions);
        });
    }
}
