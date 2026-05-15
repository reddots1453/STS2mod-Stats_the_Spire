using CommunityStats.Config;
using Godot;

namespace CommunityStats.UI;

/// <summary>
/// A small label overlay for stat displays.
/// Local edition — factory methods that used community ApiModels removed.
/// </summary>
public partial class StatsLabel : Label
{
    private static readonly Color HighWinColor = new(0.3f, 0.9f, 0.3f);
    private static readonly Color MidWinColor  = new(1f, 0.85f, 0.2f);
    private static readonly Color LowWinColor  = new(0.9f, 0.3f, 0.3f);
    private static readonly Color NeutralColor = new(0.85f, 0.85f, 0.85f);

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
        => Create(L.Get("stats.loading"), NeutralColor);

    public static StatsLabel ForUnavailable()
        => Create(L.Get("stats.no_data"), new Color(0.5f, 0.5f, 0.5f));

    public static StatsLabel ForEncounterNoData()
        => Create(L.Get("stats.no_data"), new Color(0.55f, 0.55f, 0.6f));

    public static Color WinRateColor(float winRate) => winRate switch
    {
        >= 0.6f => HighWinColor,
        >= 0.4f => MidWinColor,
        _ => LowWinColor
    };

    public static Color DeathRateColor(float deathRate) => deathRate switch
    {
        >= 0.15f => LowWinColor,
        >= 0.05f => MidWinColor,
        _ => HighWinColor
    };
}
