using System.Collections.Generic;
using System.Linq;
using CommunityStats.Config;
using Godot;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Odds;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace CommunityStats.UI;

/// <summary>
/// Hover panel for unvisited `?` map points (PRD §3.8).
///
/// Manual feedback round 4: previously this panel rendered the raw
/// `_nonEventOdds` map values which IGNORE relics that blacklist a room
/// type — Juzu Bracelet (drops Monster) and Golden Compass (forces Event
/// only). The fix now feeds the eligible-set through
/// `Hook.ModifyUnknownMapPointRoomTypes`, hides any blacklisted row, and
/// re-distributes the freed probability mass to Event so the visible
/// percentages always sum to 100%.
/// </summary>
public static class UnknownRoomPanel
{
    // Category colors (loosely following game room-type accents)
    private static readonly Color MonsterColor  = new(0.9f, 0.4f, 0.4f);
    private static readonly Color EliteColor    = new(0.95f, 0.6f, 0.25f);
    private static readonly Color TreasureColor = new(0.95f, 0.85f, 0.4f);
    private static readonly Color ShopColor     = new(0.36f, 0.66f, 0.98f);
    private static readonly Color EventColor    = new(0.5f, 0.95f, 0.6f);
    private static readonly Color CreamColor    = new("#FFF6E2");
    private static readonly Color GrayColor     = new(0.6f, 0.6f, 0.7f);

    public static InfoModPanel Create(UnknownMapPointOdds odds, IRunState? runState = null)
    {
        var panel = InfoModPanel.Create(
            L.Get("unknown.title"),
            L.Get("unknown.subtitle"));
        panel.AddSeparator();

        // Build the eligible room set (default = all four non-event types + Event).
        var eligible = ResolveEligibleRoomTypes(runState);

        // Compute the live probability map, zeroing out blacklisted entries
        // and folding their share into Event.
        float monster  = eligible.Contains(RoomType.Monster)  ? Mathf.Max(0, odds.MonsterOdds)  : 0f;
        float elite    = eligible.Contains(RoomType.Elite)    ? Mathf.Max(0, odds.EliteOdds)    : 0f;
        float treasure = eligible.Contains(RoomType.Treasure) ? Mathf.Max(0, odds.TreasureOdds) : 0f;
        float shop     = eligible.Contains(RoomType.Shop)     ? Mathf.Max(0, odds.ShopOdds)     : 0f;
        float nonEvent = monster + elite + treasure + shop;
        float ev       = eligible.Contains(RoomType.Event) ? Mathf.Max(0f, 1f - nonEvent) : 0f;

        AddRow(panel, L.Get("room.event"),    ev,        EventColor);
        AddRow(panel, L.Get("room.monster"),  monster,   MonsterColor);
        if (elite > 0f)
            AddRow(panel, L.Get("room.elite"), elite,    EliteColor);
        AddRow(panel, L.Get("room.treasure"), treasure,  TreasureColor);
        AddRow(panel, L.Get("room.shop"),     shop,      ShopColor);

        // Surface a footer note when relic-driven blacklists collapsed the set.
        if (!eligible.Contains(RoomType.Monster) || !eligible.Contains(RoomType.Treasure)
            || !eligible.Contains(RoomType.Shop))
        {
            panel.AddSeparator();
            panel.AddLabel(L.Get("unknown.relic_modified"), GrayColor);
        }

        return panel;
    }

    private static IReadOnlySet<RoomType> ResolveEligibleRoomTypes(IRunState? runState)
    {
        var basis = new HashSet<RoomType>
        {
            RoomType.Event, RoomType.Monster, RoomType.Treasure, RoomType.Shop, RoomType.Elite
        };
        if (runState == null) return basis;
        try
        {
            return Hook.ModifyUnknownMapPointRoomTypes(runState, basis);
        }
        catch
        {
            return basis;
        }
    }

    private static void AddRow(InfoModPanel panel, string label, float value, Color labelColor)
    {
        if (value <= 0f) return;
        var pct = (value * 100f).ToString("F1") + "%";
        panel.AddRow(label, pct, labelColor, labelColor);
    }
}
