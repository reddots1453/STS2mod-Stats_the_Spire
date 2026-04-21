using System.Collections.Generic;

namespace CommunityStats.Util;

/// <summary>
/// Hard-coded table of monsters whose starting move depends on runtime state
/// OUTSIDE the state machine — typically a parent-class field that picks which
/// Move to feed into `new MonsterMoveStateMachine(list, initial)`. These
/// monsters don't have a ConditionalBranchState as the initial state, so the
/// automatic "phase 1 distributed-label" rule doesn't catch them.
///
/// The table maps monsterId → (stateId → label). At render time, any state
/// listed here is forced visible (exempt from dead-cell and random-branch
/// hiding) and gets a small "X初始" label in its top-left corner, matching
/// the same visual style as phase 1's distributed labels.
///
/// Round 9 round 25: created alongside the distributed-label refactor.
/// Fabricator (whose initial IS a ConditionalBranchState) is still blocklisted
/// from phase 1 per user request — see `IntentStateMachinePanel.Create`.
/// </summary>
public static class MonsterInitialVariants
{
    private static readonly Dictionary<string, Dictionary<string, string>> _table = new()
    {
        // NOTE: values are FULL labels (panel renders them as-is, no suffix
        // appended). Most include "初始" but TerrorEel uses "时" instead.
        //
        // Inklet (墨宝): front/back rows start with JAB; middle starts with WHIRLWIND.
        // Parent field `_middleInklet` picks the initial state.
        ["INKLET"] = new()
        {
            ["JAB_MOVE"] = "前排/后排初始",
            ["WHIRLWIND_MOVE"] = "中间初始",
        },

        // Chomper (啃咬机): first spawn uses CLAMP, second uses SCREECH.
        // Parent field `_screamFirst` picks the initial state.
        ["CHOMPER"] = new()
        {
            ["CLAMP_MOVE"] = "1号位初始",
            ["SCREECH_MOVE"] = "2号位初始",
        },

        // Wriggler (扭动虫): hardcoded override rewrites SPAWNED.FollowUp = BITE
        // so INIT_MOVE becomes dead code, leaving only BITE↔WRIGGLE reachable.
        ["WRIGGLER"] = new()
        {
            ["SPAWNED_MOVE"] = "击晕状态初始",
            ["NASTY_BITE_MOVE"] = "1号位、3号位初始",
            ["WRIGGLE_MOVE"] = "2号位、4号位初始",
        },

        // TerrorEel (骇鳗): HP<50% runtime trigger force-switches to STUN
        // (STUN → TERROR → CRASH back into the main cycle). Not in the state
        // machine; surfaced here as a labeled entry. Uses "时" suffix to read
        // naturally as "HP<50%时" instead of "HP<50%初始".
        ["TERROR_EEL"] = new()
        {
            ["STUN_MOVE"] = "HP<50%时",
        },

        // ScrollOfBiting (咬人卷轴): StarterMoveIdx % 3 picks starter.
        ["SCROLL_OF_BITING"] = new()
        {
            ["CHOMP"] = "1号位初始",
            ["CHEW"] = "2号位初始",
            ["MORE_TEETH"] = "3号位初始",
        },
    };

    /// <summary>
    /// Get the distributed-label map for a given monster, or null when the
    /// monster has no runtime-determined initial variants registered.
    /// </summary>
    public static Dictionary<string, string>? Get(string? monsterId)
    {
        if (string.IsNullOrEmpty(monsterId)) return null;
        return _table.TryGetValue(monsterId!, out var dict) ? dict : null;
    }
}
