using System.Collections.Generic;

namespace CommunityStats.Util;

/// <summary>
/// Hard-coded human-readable condition labels for the 16 STS2 monsters whose
/// `MonsterMoveStateMachine` uses `ConditionalBranchState`. The conditions
/// themselves are arbitrary `Func&lt;bool&gt;` lambdas captured at construction
/// time — there's no general way to recover the predicate text at runtime
/// (compiler-generated method names are opaque, IL parsing is too brittle),
/// so we maintain this table by reading the decompiled `GenerateMoveStateMachine`
/// for each monster manually.
///
/// Each entry maps monster id → list of branch labels in **declaration order**
/// (matching `ConditionalBranchState.AddState` call order). The decode in
/// `MonsterIntentMetadata.BuildState` preserves this order, so we just index
/// by branch position.
///
/// Round 9 round 12: built from `_decompiled/.../Models.Monsters/*.cs` lines
/// `conditionalBranchState.AddState(...)`.
/// </summary>
public static class MonsterConditionHints
{
    private static readonly Dictionary<string, string[]> _table = new()
    {
        // Toadpole: runtime `IsFront` toggle. Branch declaration order in
        // the game feeds AddState(WHIRL, !IsFront) first (back = 2号位) and
        // AddState(SPIKEN, IsFront) second (front = 1号位), so this table
        // must list 2号位 before 1号位 to match index.
        ["TOADPOLE"] = new[] { "2号位", "1号位" },

        // Exoskeleton: branches by spawn slot index
        ["EXOSKELETON"] = new[] { "1号位", "2号位", "3号位", "4号位" },

        // Lagavulin Matriarch: asleep vs awake (sleeps until first hit)
        ["LAGAVULIN_MATRIARCH"] = new[] { "睡眠中", "已苏醒" },

        // Wriggler: 4-slot ensemble, branches by spawn slot
        ["WRIGGLER"] = new[] { "1号位", "2号位", "3号位", "4号位" },

        // Test Subject: branches by respawn count
        ["TEST_SUBJECT"] = new[] { "复活<2次", "复活≥2次" },

        // Slumbering Beetle: slumber stacks vs awake
        ["SLUMBERING_BEETLE"] = new[] { "沉睡中", "已苏醒" },

        // Queen: amalgam alive vs dead (TWO ConditionalBranchStates use the same pair)
        ["QUEEN"] = new[] { "组合体存活", "组合体已死亡" },

        // Phantasmal Gardener: branches by spawn slot
        ["PHANTASMAL_GARDENER"] = new[] { "1号位", "2号位", "3号位", "4号位" },

        // Ovicopter: can lay egg vs cannot
        ["OVICOPTER"] = new[] { "可产蛋", "无法产蛋" },

        // Myte: branches by spawn slot (only 2)
        ["MYTE"] = new[] { "1号位", "2号位" },

        // Nibbit: alone branch + front/back orientation (variable count, fall-through)
        // Most common pairing is 2-branch front/back at non-alone runs.
        ["NIBBIT"] = new[] { "背面", "正面", "独自一人" },

        // Living Shield: ally count check
        ["LIVING_SHIELD"] = new[] { "有友军", "无友军" },

        // Knowledge Demon: curse-of-knowledge counter
        ["KNOWLEDGE_DEMON"] = new[] { "计数<3", "计数≥3" },

        // Frog Knight: beetle charged OR HP≥50% / opposite
        ["FROG_KNIGHT"] = new[] { "甲虫已充能或HP≥50%", "甲虫未充能且HP<50%" },

        // Fabricator: condition is bot count ≤2 (can fabricate) vs ≥3 (cannot)
        ["FABRICATOR"] = new[] { "机器人≤2只", "机器人≥3只" },

        // Bowlbug Rock: balance state
        ["BOWLBUG_ROCK"] = new[] { "失衡", "平衡" },
    };

    /// <summary>
    /// Resolve the human-readable label for the i-th conditional branch of a
    /// monster's group state. Returns null when no hint is registered or the
    /// branch index exceeds the table; callers should fall back to "条件".
    /// </summary>
    public static string? Get(string? monsterId, int branchIndex)
    {
        if (string.IsNullOrEmpty(monsterId)) return null;
        if (!_table.TryGetValue(monsterId!, out var arr)) return null;
        if (branchIndex < 0 || branchIndex >= arr.Length) return null;
        return arr[branchIndex];
    }
}
