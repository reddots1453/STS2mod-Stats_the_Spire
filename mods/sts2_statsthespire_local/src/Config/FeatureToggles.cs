using System.Text.Json;
using System.Text.Json.Serialization;

namespace CommunityStats.Config;

/// <summary>
/// Per-feature toggle settings. Each sub-feature can be independently
/// enabled/disabled via the F9 settings panel.
/// Disabled features skip their Harmony postfix entirely (zero overhead).
/// </summary>
public class FeatureToggles
{
    [JsonPropertyName("contribution_panel")]
    public bool ContributionPanel { get; set; } = true;

    [JsonPropertyName("card_library_stats")]
    public bool CardLibraryStats { get; set; } = true;

    [JsonPropertyName("relic_stats")]
    public bool RelicStats { get; set; } = true;

    [JsonPropertyName("unknown_room_odds")]
    public bool UnknownRoomOdds { get; set; } = true;

    [JsonPropertyName("shop_prices")]
    public bool ShopPrices { get; set; } = true;

    [JsonPropertyName("intent_state_machine")]
    public bool IntentStateMachine { get; set; } = true;

    /// <summary>
    /// Labels for each toggle, used by FilterPanel UI.
    /// Returns (propertyName, localizedLabel) pairs.
    /// </summary>
    public static IReadOnlyList<(string Key, string LabelKey)> ToggleDefinitions { get; } = new List<(string, string)>
    {
        ("ContributionPanel",  "toggle.contribution_panel"),
        ("CardLibraryStats",   "toggle.card_library_stats"),
        ("RelicStats",         "toggle.relic_stats"),
        ("UnknownRoomOdds",    "toggle.unknown_room_odds"),
        ("ShopPrices",         "toggle.shop_prices"),
        ("IntentStateMachine", "toggle.intent_state_machine"),
    };

    public bool GetByName(string name) => name switch
    {
        "ContributionPanel"  => ContributionPanel,
        "CardLibraryStats"   => CardLibraryStats,
        "RelicStats"         => RelicStats,
        "UnknownRoomOdds"    => UnknownRoomOdds,
        "ShopPrices"         => ShopPrices,
        "IntentStateMachine" => IntentStateMachine,
        _ => true
    };

    public void SetByName(string name, bool value)
    {
        switch (name)
        {
            case "ContributionPanel":  ContributionPanel = value; break;
            case "CardLibraryStats":   CardLibraryStats = value; break;
            case "RelicStats":         RelicStats = value; break;
            case "UnknownRoomOdds":    UnknownRoomOdds = value; break;
            case "ShopPrices":         ShopPrices = value; break;
            case "IntentStateMachine": IntentStateMachine = value; break;
        }
    }
}
