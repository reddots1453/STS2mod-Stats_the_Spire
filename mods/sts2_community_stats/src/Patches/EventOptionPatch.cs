using CommunityStats.Collection;
using CommunityStats.Util;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Events;

namespace CommunityStats.Patches;

[HarmonyPatch]
public static class EventOptionPatch
{
    private const string StatsLabelMeta = "community_stats_label";

    // PRD §3.13: track live event option buttons for DataRefreshed re-render.
    private static readonly List<WeakReference<NEventOptionButton>> _liveButtons = new();

    public static void SubscribeRefresh()
    {
        Api.StatsProvider.DataRefreshed += OnDataRefreshed;
    }

    private static void OnDataRefreshed()
    {
        Safe.Run(() =>
        {
            for (int i = _liveButtons.Count - 1; i >= 0; i--)
            {
                if (_liveButtons[i].TryGetTarget(out var btn) &&
                    GodotObject.IsInstanceValid(btn) && btn.IsInsideTree())
                    AfterOptionReady(btn);
                else
                    _liveButtons.RemoveAt(i);
            }
        });
    }

    private static void TrackButton(NEventOptionButton btn)
    {
        for (int i = _liveButtons.Count - 1; i >= 0; i--)
        {
            if (!_liveButtons[i].TryGetTarget(out var t) || !GodotObject.IsInstanceValid(t))
                _liveButtons.RemoveAt(i);
            else if (t == btn) return;
        }
        _liveButtons.Add(new WeakReference<NEventOptionButton>(btn));
    }

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

    [HarmonyPatch(typeof(NEventOptionButton), nameof(NEventOptionButton._Ready))]
    [HarmonyPostfix]
    public static void AfterOptionReady(NEventOptionButton __instance)
    {
        TrackButton(__instance);
        if (!Config.ModConfig.Toggles.EventPickRate) return;
        Safe.Run(() =>
        {
            var eventModel = __instance.Event;
            if (eventModel == null) return;

            var eventId = eventModel.Id.Entry;
            var index = Traverse.Create(__instance).Property("Index").GetValue<int>();

            // Remove any existing label
            foreach (var child in __instance.GetChildren())
            {
                if (child is Label lbl && lbl.HasMeta(StatsLabelMeta))
                    lbl.QueueFree();
            }

            var eventStats = Api.StatsProvider.Instance.GetEventStats(eventId);

            // ── Try combo-based stats (ancient events) ──────────
            if (eventStats?.Combos != null && eventStats.Combos.Count > 0)
            {
                AttachComboStats(__instance, eventStats);
                return;
            }

            // ── Try flat option stats (COLORFUL_PHILOSOPHERS) ───
            if (eventStats?.FlatOptions != null && eventStats.FlatOptions.Count > 0)
            {
                var optionName = ExtractOptionName(__instance.Option?.TextKey);
                if (optionName != null)
                {
                    var flat = eventStats.FlatOptions.FirstOrDefault(
                        f => string.Equals(f.OptionId, optionName, System.StringComparison.OrdinalIgnoreCase));
                    if (flat != null)
                    {
                        AttachLabel(__instance, UI.StatsLabel.ForComboOption(flat));
                        return;
                    }
                }
                AttachLabel(__instance, UI.StatsLabel.ForUnavailable());
                return;
            }

            // ── Static option stats (index-based) ──────────────
            Label label;
            if (eventStats != null)
            {
                var optionStats = eventStats.Options?.FirstOrDefault(o => o.OptionIndex == index);
                if (optionStats != null)
                    label = UI.StatsLabel.ForEventOption(optionStats);
                else
                    label = UI.StatsLabel.ForUnavailable();
            }
            else if (!Api.StatsProvider.Instance.HasBundle)
            {
                label = UI.StatsLabel.ForLoading();
            }
            else
            {
                label = UI.StatsLabel.ForUnavailable();
            }

            AttachLabel(__instance, label);
        });
    }

    /// <summary>
    /// For combo events (ancients): collect all sibling option buttons,
    /// build combo_key, look up per-combo stats, and attach labels.
    /// If not all siblings are ready yet, this button defers — the last
    /// sibling's _Ready will handle all buttons.
    /// </summary>
    private static void AttachComboStats(NEventOptionButton button, Api.EventStats eventStats)
    {
        // Walk up to find the NEventLayout parent and get all option buttons
        var parent = button.GetParent();
        if (parent == null) return;

        var siblings = new System.Collections.Generic.List<NEventOptionButton>();
        foreach (var child in parent.GetChildren())
        {
            if (child is NEventOptionButton btn)
                siblings.Add(btn);
        }

        // Check if we know all the expected options (use eventModel.CurrentOptions)
        var eventModel = button.Event;
        var expectedCount = eventModel?.CurrentOptions?.Count ?? siblings.Count;
        if (siblings.Count < expectedCount)
            return; // Not all siblings ready; last one will trigger for all

        // Build combo_key from sorted option names
        var optionNames = new System.Collections.Generic.List<string>(siblings.Count);
        var buttonOptionMap = new System.Collections.Generic.Dictionary<NEventOptionButton, string>();
        foreach (var sib in siblings)
        {
            var name = ExtractOptionName(sib.Option?.TextKey);
            if (name == null)
            {
                // Can't resolve option name, fall back to unavailable
                foreach (var s in siblings)
                    AttachLabel(s, UI.StatsLabel.ForUnavailable());
                return;
            }
            optionNames.Add(name);
            buttonOptionMap[sib] = name;
        }

        optionNames.Sort(System.StringComparer.Ordinal);
        var comboKey = string.Join("|", optionNames);

        // Look up this combo
        if (eventStats.Combos!.TryGetValue(comboKey, out var comboOptions))
        {
            foreach (var sib in siblings)
            {
                // Remove any existing stats label
                foreach (var child in sib.GetChildren())
                {
                    if (child is Label lbl && lbl.HasMeta(StatsLabelMeta))
                        lbl.QueueFree();
                }

                var myName = buttonOptionMap[sib];
                var stats = comboOptions.FirstOrDefault(
                    c => string.Equals(c.OptionId, myName, System.StringComparison.OrdinalIgnoreCase));
                if (stats != null)
                    AttachLabel(sib, UI.StatsLabel.ForComboOption(stats));
                else
                    AttachLabel(sib, UI.StatsLabel.ForUnavailable());
            }
        }
        else
        {
            // Combo not found in data — show unavailable for all
            foreach (var sib in siblings)
            {
                foreach (var child in sib.GetChildren())
                {
                    if (child is Label lbl && lbl.HasMeta(StatsLabelMeta))
                        lbl.QueueFree();
                }
                AttachLabel(sib, UI.StatsLabel.ForUnavailable());
            }
        }
    }

    /// <summary>
    /// Extract option name from a TextKey like "OROBAS.pages.INITIAL.options.SEA_GLASS".
    /// </summary>
    private static string? ExtractOptionName(string? textKey)
    {
        if (string.IsNullOrEmpty(textKey) || !textKey.Contains(".options."))
            return null;
        var after = textKey[(textKey.IndexOf(".options.", System.StringComparison.Ordinal) + 9)..];
        var dot = after.IndexOf('.');
        return dot > 0 ? after[..dot] : after;
    }

    private static void AttachLabel(NEventOptionButton button, Label label)
    {
        label.SetMeta(StatsLabelMeta, true);
        button.AddChild(label);
    }
}
