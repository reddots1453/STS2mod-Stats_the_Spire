using System.Collections.Generic;
using CommunityStats.Config;

namespace CommunityStats.Util;

/// <summary>
/// Hard-coded human-readable condition labels for STS2 monsters whose
/// `MonsterMoveStateMachine` uses `ConditionalBranchState`. Each entry maps
/// monsterId → list of (cn, en) labels in declaration order.
/// </summary>
public static class MonsterConditionHints
{
    private static readonly Dictionary<string, (string cn, string en)[]> _table = new()
    {
        ["TOADPOLE"] = new[] { ("2号位", "Pos 2"), ("1号位", "Pos 1") },
        ["EXOSKELETON"] = new[] { ("1号位", "Pos 1"), ("2号位", "Pos 2"), ("3号位", "Pos 3"), ("4号位", "Pos 4") },
        ["LAGAVULIN_MATRIARCH"] = new[] { ("睡眠中", "Asleep"), ("已苏醒", "Awake") },
        ["WRIGGLER"] = new[] { ("1号位", "Pos 1"), ("2号位", "Pos 2"), ("3号位", "Pos 3"), ("4号位", "Pos 4") },
        ["TEST_SUBJECT"] = new[] { ("复活<2次", "Revived &lt;2"), ("复活≥2次", "Revived ≥2") },
        ["SLUMBERING_BEETLE"] = new[] { ("沉睡中", "Asleep"), ("已苏醒", "Awake") },
        ["QUEEN"] = new[] { ("组合体存活", "Amalgam alive"), ("组合体已死亡", "Amalgam dead") },
        ["PHANTASMAL_GARDENER"] = new[] { ("1号位", "Pos 1"), ("2号位", "Pos 2"), ("3号位", "Pos 3"), ("4号位", "Pos 4") },
        ["OVICOPTER"] = new[] { ("可产蛋", "Can lay egg"), ("无法产蛋", "Cannot lay egg") },
        ["MYTE"] = new[] { ("1号位", "Pos 1"), ("2号位", "Pos 2") },
        ["NIBBIT"] = new[] { ("背面", "Back"), ("正面", "Front"), ("独自一人", "Alone") },
        ["LIVING_SHIELD"] = new[] { ("有友军", "Has ally"), ("无友军", "No ally") },
        ["KNOWLEDGE_DEMON"] = new[] { ("计数<3", "Count &lt;3"), ("计数≥3", "Count ≥3") },
        ["FROG_KNIGHT"] = new[] { ("甲虫已充能或HP≥50%", "Beetle charged / HP≥50%"), ("甲虫未充能且HP<50%", "Beetle uncharged & HP&lt;50%") },
        ["FABRICATOR"] = new[] { ("机器人≤2只", "Bots ≤2"), ("机器人≥3只", "Bots ≥3") },
        ["BOWLBUG_ROCK"] = new[] { ("失衡", "Unstable"), ("平衡", "Stable") },
    };

    public static string? Get(string? monsterId, int branchIndex)
    {
        if (string.IsNullOrEmpty(monsterId)) return null;
        if (!_table.TryGetValue(monsterId!, out var arr)) return null;
        if (branchIndex < 0 || branchIndex >= arr.Length) return null;
        return L.Current == L.Lang.EN ? arr[branchIndex].en : arr[branchIndex].cn;
    }
}
