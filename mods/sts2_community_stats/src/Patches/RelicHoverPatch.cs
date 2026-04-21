using CommunityStats.Api;
using CommunityStats.Config;
using CommunityStats.UI;
using CommunityStats.Util;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Relics;

namespace CommunityStats.Patches;

/// <summary>
/// Patches both NRelicBasicHolder and NRelicInventoryHolder hover to show relic win/pick rate stats.
/// NRelicBasicHolder is used in reward screens etc.; NRelicInventoryHolder is the in-game relic bar.
/// Both have the same field layout: private RelicModel _model, public NRelic Relic => _relic.
/// </summary>
[HarmonyPatch]
public static class RelicHoverPatch
{
    private const string StatsLabelMeta = "cs_relic_stats";

    // PRD §3.13: no-op — hover tooltips are ephemeral. If the user is not
    // currently hovering a relic there is nothing to refresh; if they are,
    // unfocus+refocus will regenerate the label from fresh StatsProvider
    // data anyway. Method is kept so CommunityStatsMod can wire it up
    // alongside the other patches.
    public static void SubscribeRefresh()
    {
        // intentionally empty
    }

    // Both holders show the same "win rate + delta" text — no pick rate.

    // ── NRelicBasicHolder (reward screens, etc.) ────────────────

    [HarmonyPatch(typeof(NRelicBasicHolder), "OnFocus")]
    [HarmonyPostfix]
    public static void AfterBasicOnFocus(NRelicBasicHolder __instance)
    {
        if (!ModConfig.Toggles.RelicStats) return;
        Safe.Run(() => ShowRelicStats(__instance, __instance.Relic));
    }

    [HarmonyPatch(typeof(NRelicBasicHolder), "OnUnfocus")]
    [HarmonyPostfix]
    public static void AfterBasicOnUnfocus(NRelicBasicHolder __instance)
    {
        Safe.Run(() => RemoveRelicStats(__instance));
    }

    // ── NRelicInventoryHolder (in-game top-bar relic bar) ───────

    [HarmonyPatch(typeof(NRelicInventoryHolder), "OnFocus")]
    [HarmonyPostfix]
    public static void AfterInventoryOnFocus(NRelicInventoryHolder __instance)
    {
        if (!ModConfig.Toggles.RelicStats) return;
        Safe.Run(() => ShowRelicStats(__instance, __instance.Relic));
    }

    [HarmonyPatch(typeof(NRelicInventoryHolder), "OnUnfocus")]
    [HarmonyPostfix]
    public static void AfterInventoryOnUnfocus(NRelicInventoryHolder __instance)
    {
        Safe.Run(() => RemoveRelicStats(__instance));
    }

    // ── Shared logic ────────────────────────────────────────────

    private static void ShowRelicStats(Control holder, NRelic? relic)
    {
        if (holder.HasMeta(StatsLabelMeta)) return;

        string? relicId = null;
        try
        {
            relicId = relic?.Model?.Id.Entry;
        }
        catch
        {
            // Model not yet set (e.g., before _Ready)
        }

        if (string.IsNullOrEmpty(relicId))
        {
            // Fallback: access private _model field via Traverse
            var model = Traverse.Create(holder).Field("_model").GetValue<object>();
            if (model == null) return;
            var id = Traverse.Create(model).Property("Id").GetValue<object>();
            relicId = Traverse.Create(id).Property("Entry").GetValue<string>();
        }

        if (string.IsNullOrEmpty(relicId)) return;

        Safe.Info($"[DIAG:RelicHover] relicId={relicId} hasBundle={StatsProvider.Instance.HasBundle}");

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
                label = StatsLabel.ForRelicWinRateWithDelta(stats, globalAvg);
            }
            else
            {
                label = StatsLabel.ForUnavailable();
            }
        }

        // Position below the relic icon
        label.Position = new Vector2(-30, holder.Size.Y + 2);
        label.ZIndex = 100;
        holder.AddChild(label);
        holder.SetMeta(StatsLabelMeta, true);
    }

    private static void RemoveRelicStats(Control holder)
    {
        if (!holder.HasMeta(StatsLabelMeta)) return;

        foreach (var child in holder.GetChildren())
        {
            if (child is StatsLabel)
            {
                child.QueueFree();
                break;
            }
        }
        holder.RemoveMeta(StatsLabelMeta);
    }
}
