using System.Collections.Generic;
using CommunityStats.Config;

namespace CommunityStats.Util;

/// <summary>
/// Hard-coded table of monsters whose starting move depends on runtime state
/// OUTSIDE the state machine. Maps monsterId → (stateId → localised label).
/// The label is chosen based on L.Current at render time.
/// </summary>
public static class MonsterInitialVariants
{
    // Each entry: stateId → (cn, en)
    private static readonly Dictionary<string, Dictionary<string, (string cn, string en)>> _table = new()
    {
        ["INKLET"] = new()
        {
            ["JAB_MOVE"] = ("前排/后排初始", "Front/Back initial"),
            ["WHIRLWIND_MOVE"] = ("中间初始", "Middle initial"),
        },
        ["CHOMPER"] = new()
        {
            ["CLAMP_MOVE"] = ("1号位初始", "Pos 1 initial"),
            ["SCREECH_MOVE"] = ("2号位初始", "Pos 2 initial"),
        },
        ["WRIGGLER"] = new()
        {
            ["SPAWNED_MOVE"] = ("击晕状态初始", "Stunned initial"),
            ["NASTY_BITE_MOVE"] = ("1号位、3号位初始", "Pos 1,3 initial"),
            ["WRIGGLE_MOVE"] = ("2号位、4号位初始", "Pos 2,4 initial"),
        },
        ["TERROR_EEL"] = new()
        {
            ["STUN_MOVE"] = ("HP<50%时", "HP<50%"),
        },
        ["SCROLL_OF_BITING"] = new()
        {
            ["CHOMP"] = ("1号位初始", "Pos 1 initial"),
            ["CHEW"] = ("2号位初始", "Pos 2 initial"),
            ["MORE_TEETH"] = ("3号位初始", "Pos 3 initial"),
        },
    };

    /// <summary>
    /// Get the distributed-label map for a given monster, or null.
    /// Labels are resolved from (cn, en) tuples based on L.Current.
    /// </summary>
    public static Dictionary<string, string>? Get(string? monsterId)
    {
        if (string.IsNullOrEmpty(monsterId)) return null;
        if (!_table.TryGetValue(monsterId!, out var dict)) return null;
        var result = new Dictionary<string, string>();
        foreach (var (stateId, (cn, en)) in dict)
            result[stateId] = L.Current == L.Lang.EN ? en : cn;
        return result;
    }
}
