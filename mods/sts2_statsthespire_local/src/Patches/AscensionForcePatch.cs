using CommunityStats.Util;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Ascension;
using MegaCrit.Sts2.Core.Runs;

namespace CommunityStats.Patches;

/// <summary>
/// Round 9 round 9: while <see cref="MonsterIntentMetadata.ForcedAscensionDeadly"/>
/// is non-null, intercept <see cref="RunManager.HasAscension"/> and return the
/// forced value for the two ascension levels that affect monster intent data:
/// <c>DeadlyEnemies</c> (A9 — increased damage / new moves) and
/// <c>ToughEnemies</c> (A8 — bundled with A9 in the "deadly" tier per spec §3.10.7).
/// All other ascension levels and all calls outside the bake window pass through
/// to the real implementation by returning <c>true</c> from the prefix.
///
/// This is the mechanism that lets <see cref="MonsterIntentMetadata.Initialize"/>
/// bake every monster TWICE (once at Standard tier, once at Deadly tier) at mod
/// init time when there's no run in progress, so hover can show the correct data
/// instantly regardless of which ascension the player picked.
/// </summary>
[HarmonyPatch(typeof(RunManager), nameof(RunManager.HasAscension))]
public static class AscensionForcePatch
{
    [HarmonyPrefix]
    public static bool Prefix(AscensionLevel level, ref bool __result)
    {
        var forced = MonsterIntentMetadata.ForcedAscensionDeadly;
        if (forced == null) return true; // pass through

        if (level == AscensionLevel.DeadlyEnemies || level == AscensionLevel.ToughEnemies)
        {
            __result = forced.Value;
            return false; // skip original
        }
        return true; // other levels: pass through
    }
}
