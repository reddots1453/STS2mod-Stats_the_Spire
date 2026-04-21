// Round 6 fix: this patch used to add a SECOND HarmonyPostfix on
// `CombatHistory.CardPlayFinished` and `CombatHistory.PotionUsed`
// to fire `CombatTracker.NotifyCombatDataUpdated()`. The user reported
// the live refresh never running; the most plausible cause was the
// duplicate-postfix interaction. The notification has been moved
// directly into `CombatHistoryPatch.AfterCardPlayFinished`, so this file
// is now empty (kept to preserve git history).

namespace CommunityStats.Patches;

// (intentionally empty — see CombatHistoryPatch.AfterCardPlayFinished)
public static class RealTimeCombatPatch
{
}
