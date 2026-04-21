using System.Collections.Generic;

namespace CommunityStats.Collection;

/// <summary>
/// Per-relic aggregated statistics computed from the user's local
/// RunHistory files. Used by `RelicLibraryPatch` to populate the "Mine"
/// column of the compendium relic stats panel (PRD §3.3 round 6).
///
/// Counts:
///   RunsWith  — number of distinct *runs* that contained this relic at any time
///   WinsWith  — number of distinct *runs* that contained this relic AND won
///
/// WinRate = WinsWith / RunsWith
/// </summary>
public sealed class LocalRelicStats
{
    public string RelicId { get; init; } = "";
    public int RunsWith { get; init; }
    public int WinsWith { get; init; }

    public float WinRate => RunsWith > 0 ? (float)WinsWith / RunsWith : 0f;
}

/// <summary>
/// Snapshot of all local relic aggregations + total runs counted, returned
/// as one immutable bundle so consumers can compare against the community
/// dataset's TotalRuns side-by-side.
/// </summary>
public sealed class LocalRelicStatsBundle
{
    public int TotalRuns { get; init; }
    public IReadOnlyDictionary<string, LocalRelicStats> Relics { get; init; }
        = new Dictionary<string, LocalRelicStats>();

    /// <summary>Average win rate across every relic that has at least one run.</summary>
    public float AverageWinRate { get; init; }

    public LocalRelicStats? Get(string relicId) =>
        Relics.TryGetValue(relicId, out var s) ? s : null;

    public static readonly LocalRelicStatsBundle Empty = new()
    {
        TotalRuns = 0,
        Relics = new Dictionary<string, LocalRelicStats>(),
        AverageWinRate = 0f,
    };
}
