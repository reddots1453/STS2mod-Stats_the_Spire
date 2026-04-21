using System.Collections.Generic;

namespace CommunityStats.Collection;

/// <summary>
/// Mid-run save/restore container for the live combat tracker state.
/// PRD §3.6.1 round 8 — when the player saves and quits during a run we
/// flush this to `{cache}/contributions/{seed}_live.json` and rehydrate
/// it on the next mod boot so the contribution panel keeps the same
/// 本场战斗 / 本局汇总 totals across the save+quit boundary.
/// </summary>
public sealed class LiveContributionSnapshot
{
    public string EncounterId { get; set; } = "";
    public string EncounterType { get; set; } = "";
    public int Floor { get; set; }
    public int TurnCount { get; set; }
    public int TotalDamageDealt { get; set; }
    public int DamageTakenByPlayer { get; set; }
    public bool CombatInProgress { get; set; }

    /// <summary>Per-source totals for the in-progress combat (null if no combat).</summary>
    public Dictionary<string, ContributionAccum>? CurrentCombat { get; set; }

    /// <summary>Per-source totals across the entire run so far.</summary>
    public Dictionary<string, ContributionAccum>? RunTotals { get; set; }
}
