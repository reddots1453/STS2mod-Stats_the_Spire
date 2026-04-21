using CommunityStats.Api;
using CommunityStats.Config;
using CommunityStats.Util;
using Godot;

namespace CommunityStats.UI;

/// <summary>
/// A small label overlay showing pick rate / win rate (or relic win rate, event %, etc).
/// Attach as a child of the target UI element.
/// </summary>
public partial class StatsLabel : Label
{
    private static readonly Color HighWinColor = new(0.3f, 0.9f, 0.3f);   // green
    private static readonly Color MidWinColor = new(1f, 0.85f, 0.2f);     // yellow
    private static readonly Color LowWinColor = new(0.9f, 0.3f, 0.3f);    // red
    private static readonly Color NeutralColor = new(0.85f, 0.85f, 0.85f); // light gray

    public static StatsLabel Create(string text, Color? color = null)
    {
        var label = new StatsLabel();
        label.Text = text;
        label.AddThemeColorOverride("font_color", color ?? NeutralColor);
        label.AddThemeFontSizeOverride("font_size", 12);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.VerticalAlignment = VerticalAlignment.Center;
        label.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        return label;
    }

    // ── Factory methods for common stat displays ────────────

    public static StatsLabel ForCardStats(CardStats stats)
    {
        var text = string.Format(L.Get("stats.pick"),
            (stats.PickRate * 100).ToString("F1"),
            (stats.WinRate * 100).ToString("F1"));
        return Create(text, WinRateColor(stats.WinRate));
    }

    public static StatsLabel ForRelicStats(RelicStats stats)
    {
        var text = string.Format(L.Get("stats.relic"),
            (stats.PickRate * 100).ToString("F1"),
            (stats.WinRate * 100).ToString("F1"));
        return Create(text, WinRateColor(stats.WinRate));
    }

    /// <summary>
    /// Relic stats with delta vs global average win rate (PRD 3.3/3.4).
    /// Format: "Pick X% | Win Y% (±Z%)". Colored by delta sign.
    /// </summary>
    public static StatsLabel ForRelicStatsWithDelta(RelicStats stats, float globalAvgWinRate)
    {
        var deltaPct = (stats.WinRate - globalAvgWinRate) * 100f;
        var sign = deltaPct >= 0 ? "+" : "";
        var baseText = string.Format(L.Get("stats.relic"),
            (stats.PickRate * 100).ToString("F1"),
            (stats.WinRate * 100).ToString("F1"));
        var text = $"{baseText} ({sign}{deltaPct:F1}%)";
        var color = MathF.Abs(deltaPct) < 1f
            ? NeutralColor
            : (deltaPct >= 0 ? HighWinColor : LowWinColor);
        return Create(text, color);
    }

    /// <summary>
    /// Owned-relic hover variant: win rate + delta only, no pick rate.
    /// Used by the top-bar relic inventory where the player already owns
    /// the relic and pick rate is irrelevant.
    /// Format: "Win Y% (±Z%)". Colored by delta sign.
    /// </summary>
    public static StatsLabel ForRelicWinRateWithDelta(RelicStats stats, float globalAvgWinRate)
    {
        var deltaPct = (stats.WinRate - globalAvgWinRate) * 100f;
        var sign = deltaPct >= 0 ? "+" : "";
        var baseText = string.Format(L.Get("stats.relic_win_only"),
            (stats.WinRate * 100).ToString("F1"));
        var text = $"{baseText} ({sign}{deltaPct:F1}%)";
        var color = MathF.Abs(deltaPct) < 1f
            ? NeutralColor
            : (deltaPct >= 0 ? HighWinColor : LowWinColor);
        return Create(text, color);
    }

    public static StatsLabel ForEventOption(EventOptionStats stats)
    {
        // PRD 3.5: show only selection rate, drop win rate
        var text = string.Format(L.Get("stats.event_pick"),
            (stats.SelectionRate * 100).ToString("F1"));
        return Create(text, NeutralColor);
    }

    public static StatsLabel ForComboOption(ComboOptionStats stats)
    {
        var text = string.Format(L.Get("stats.event_pick"),
            (stats.SelectionRate * 100).ToString("F1"));
        return Create(text, NeutralColor);
    }

    public static StatsLabel ForEncounter(EncounterStats stats)
    {
        var text = string.Format(L.Get("stats.encounter"),
            (stats.DeathRate * 100).ToString("F1"),
            stats.AvgDamageTaken.ToString("F0"));
        return Create(text, DeathRateColor(stats.DeathRate));
    }

    /// <summary>
    /// Round 15: placeholder label for traveled combat nodes whose encounter
    /// has no community samples yet. Shown in a muted grey so the player
    /// sees "this elite was fought but nobody else has it" instead of a
    /// silently missing overlay.
    /// </summary>
    public static StatsLabel ForEncounterNoData()
    {
        var label = Create(L.Get("stats.no_data"), new Color(0.55f, 0.55f, 0.6f));
        return label;
    }

    public static StatsLabel ForUpgradeRate(CardStats stats)
    {
        // PRD AC-15: percentages displayed with 1 decimal place.
        var text = string.Format(L.Get("stats.upgrade"),
            (stats.UpgradeRate * 100).ToString("F1"),
            (stats.WinRate * 100).ToString("F1"));
        return Create(text, WinRateColor(stats.WinRate));
    }

    public static StatsLabel ForRemovalRate(CardStats stats)
    {
        var text = string.Format(L.Get("stats.remove"),
            (stats.RemovalRate * 100).ToString("F1"),
            (stats.WinRate * 100).ToString("F1"));
        return Create(text, WinRateColor(stats.WinRate));
    }

    public static StatsLabel ForShopBuyRate(float shopBuyRate, int fontSize = 16)
    {
        var text = string.Format(L.Get("stats.buy"),
            (shopBuyRate * 100).ToString("F1"));
        var label = new StatsLabel();
        label.Text = text;
        label.AddThemeColorOverride("font_color", NeutralColor);
        label.AddThemeFontSizeOverride("font_size", fontSize);
        label.HorizontalAlignment = HorizontalAlignment.Right;
        label.VerticalAlignment = VerticalAlignment.Bottom;
        label.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
        return label;
    }

    public static StatsLabel ForLoading()
    {
        return Create(L.Get("stats.loading"), NeutralColor);
    }

    public static StatsLabel ForUnavailable()
    {
        return Create(L.Get("stats.no_data"), new Color(0.5f, 0.5f, 0.5f));
    }

    // ── Color helpers ───────────────────────────────────────

    public static Color WinRateColor(float winRate) => winRate switch
    {
        >= 0.6f => HighWinColor,
        >= 0.4f => MidWinColor,
        _ => LowWinColor
    };

    public static Color DeathRateColor(float deathRate) => deathRate switch
    {
        >= 0.15f => LowWinColor,   // high death = red
        >= 0.05f => MidWinColor,
        _ => HighWinColor
    };
}
