using CommunityStats.Api;
using CommunityStats.Config;
using CommunityStats.UI;
using CommunityStats.Util;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.UI;
using MegaCrit.Sts2.Core.Nodes.Screens.RelicCollection;

namespace CommunityStats.Patches;

/// <summary>
/// LEGACY (manual feedback round 4): the per-entry hover label was
/// replaced by `RelicLibraryPatch` which injects the stats into the
/// large NInspectRelicScreen on click — same flow as the card library.
///
/// The hover behaviour is kept disabled by default but the patch class
/// is preserved so any future "show on hover" toggle can flip it back on
/// without re-introducing the file.
/// </summary>
public static class RelicCollectionPatch
{
    private const string StatsLabelMeta = "sts_relic_collection_stats";

    // PRD §3.13: no-op — this patch is legacy/disabled (see class summary),
    // so there are no persistent labels to refresh. Kept for API symmetry
    // with the other SubscribeRefresh wire-ups in CommunityStatsMod.
    public static void SubscribeRefresh()
    {
        // intentionally empty
    }

    // Hover injection deliberately disabled — see class summary.
    public static void AfterOnFocus(NRelicCollectionEntry __instance)
    {
        // no-op
    }

    public static void AfterOnUnfocus(NRelicCollectionEntry __instance)
    {
        // no-op
    }

    private static void ShowStats(NRelicCollectionEntry entry)
    {
        if (entry.HasMeta(StatsLabelMeta)) return;
        if (entry.ModelVisibility != ModelVisibility.Visible) return;

        var relic = entry.relic;
        if (relic == null) return;

        string? relicId = null;
        try { relicId = relic.Id.Entry; } catch { }
        if (string.IsNullOrEmpty(relicId)) return;

        StatsLabel label;
        if (!StatsProvider.Instance.HasBundle)
        {
            label = StatsLabel.ForLoading();
        }
        else
        {
            var stats = StatsProvider.Instance.GetRelicStats(relicId);
            if (stats != null)
            {
                var globalAvg = StatsProvider.Instance.GetGlobalAverageRelicWinRate();
                label = StatsLabel.ForRelicStatsWithDelta(stats, globalAvg);
            }
            else
            {
                label = StatsLabel.ForUnavailable();
            }
        }

        label.Position = new Vector2(-30, entry.Size.Y + 2);
        label.ZIndex = 100;
        entry.AddChild(label);
        entry.SetMeta(StatsLabelMeta, true);
    }

    private static void RemoveStats(NRelicCollectionEntry entry)
    {
        if (!entry.HasMeta(StatsLabelMeta)) return;

        foreach (var child in entry.GetChildren())
        {
            if (child is StatsLabel)
            {
                child.QueueFree();
                break;
            }
        }
        entry.RemoveMeta(StatsLabelMeta);
    }
}
