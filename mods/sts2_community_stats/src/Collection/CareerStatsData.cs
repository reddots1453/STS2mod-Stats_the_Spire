using System.Collections.Generic;
using System.Linq;

namespace CommunityStats.Collection;

/// <summary>
/// Immutable career statistics aggregated from a player's RunHistory files.
/// Built by RunHistoryAnalyzer; consumed by CareerStatsSection (Stats screen)
/// and RunHistoryStatsSection (Run History screen).
///
/// All counts are per-character (or "all" when CharacterFilter is null).
/// "Per-Act averages" are means across all completed Acts in matching runs.
///
/// PRD-04 §3.11 (career stats display) + §3.12 (per-run stats display).
/// </summary>
public sealed class CareerStatsData
{
    /// <summary>
    /// Character filter that produced this snapshot.
    /// null = all characters combined.
    /// </summary>
    public string? CharacterFilter { get; init; }

    /// <summary>
    /// Round 9 round 46: ascension floor used to filter the runs that produced
    /// this snapshot. 0 = no filter (all runs).
    /// </summary>
    public int MinAscension { get; init; }

    /// <summary>How many runs were considered (after character filter).</summary>
    public int TotalRuns { get; init; }

    /// <summary>How many of those runs were victories.</summary>
    public int Wins { get; init; }

    /// <summary>
    /// Round 9 round 49: longest consecutive-win streak among the filtered
    /// runs (chronological order). Computed at snapshot build time.
    /// </summary>
    public int MaxWinStreak { get; init; }

    /// <summary>
    /// Round 9 round 52: the most recent consecutive-win streak. Counts wins
    /// from the newest run backwards until hitting a loss. Shown alongside
    /// MaxWinStreak as "current / max" on the summary card.
    /// </summary>
    public int CurrentWinStreak { get; init; }

    /// <summary>
    /// Rolling win rates over the last N runs (chronologically reversed = newest first).
    /// Keys: 10, 50, int.MaxValue (= "all"). Values: 0..1.
    /// </summary>
    public IReadOnlyDictionary<int, float> WinRateByWindow { get; init; } = new Dictionary<int, float>();

    /// <summary>
    /// Death cause ranking, grouped by Act index (1..4 inclusive).
    /// Each Act -> ordered list (descending count) of (encounterId, count).
    /// Encounter ids combine KilledByEncounter / KilledByEvent.
    /// "ABANDONED" if neither field is set.
    /// </summary>
    public IReadOnlyDictionary<int, IReadOnlyList<DeathEntry>> DeathCausesByAct { get; init; }
        = new Dictionary<int, IReadOnlyList<DeathEntry>>();

    /// <summary>
    /// Average path counts per Act (1..4). PRD §3.11.
    /// </summary>
    public IReadOnlyDictionary<int, ActPathStats> PathStatsByAct { get; init; }
        = new Dictionary<int, ActPathStats>();

    /// <summary>
    /// Ancient relic pick rates (PRD §3.11 ancient option choice).
    /// Key: option text key (from AncientChoiceHistoryEntry.TextKey).
    /// Value: pick rate (0..1) computed as picks / opportunities.
    /// </summary>
    public IReadOnlyDictionary<string, AncientChoiceStats> AncientPickRates { get; init; }
        = new Dictionary<string, AncientChoiceStats>();

    /// <summary>
    /// Boss damage taken stats. Key: boss encounter id (from MapPointHistory of MapPointType.Boss).
    /// Value: average DamageTaken across all encounters with that boss + death rate.
    /// </summary>
    public IReadOnlyDictionary<string, BossEncounterStats> BossStats { get; init; }
        = new Dictionary<string, BossEncounterStats>();

    /// <summary>
    /// Per-Elder breakdown of Ancient relic encounters. PRD §3.11 dropdown structure:
    /// elder → list of options → list of relics with picks/wins/delta.
    /// Key: elder encounter id (from RunHistoryPlayer.AncientChoices grouping).
    /// </summary>
    public IReadOnlyDictionary<string, ElderEntry> AncientByElder { get; init; }
        = new Dictionary<string, ElderEntry>();

    /// <summary>true when no usable history was found.</summary>
    public bool IsEmpty => TotalRuns == 0;

    public static CareerStatsData Empty(string? characterFilter, int minAscension = 0) => new()
    {
        CharacterFilter = characterFilter,
        MinAscension = minAscension,
        TotalRuns = 0,
        Wins = 0,
    };
}

public enum DeathSource
{
    Combat,    // KilledByEncounter (combat room)
    Event,     // KilledByEvent
    Abandoned, // neither set, no resolved combat fallback
}

/// <summary>One row in the death-cause ranking.</summary>
public sealed class DeathEntry
{
    public string EncounterId { get; init; } = "";
    public int Count { get; init; }
    /// <summary>Count / total deaths in this Act (0..1).</summary>
    public float Share { get; init; }
    /// <summary>Round 9 round 47: source room type for icon selection.</summary>
    public DeathSource Source { get; init; }
}

/// <summary>Per-Act averaged path counts.</summary>
public sealed class ActPathStats
{
    public float CardsGained { get; init; }
    public float CardsBought { get; init; }
    public float CardsRemoved { get; init; }
    public float CardsUpgraded { get; init; }
    public float UnknownRooms { get; init; }
    public float MonsterRooms { get; init; }
    public float EliteRooms { get; init; }
    public float ShopRooms { get; init; }
    public float CampfireRooms { get; init; }
    /// <summary>Number of runs that contributed to this Act average.</summary>
    public int SampleSize { get; init; }
}

/// <summary>Ancient relic option pick statistics.</summary>
public sealed class AncientChoiceStats
{
    public string TextKey { get; init; } = "";
    /// <summary>Times this option was offered (i.e. encountered the parent ancient event).</summary>
    public int Opportunities { get; init; }
    /// <summary>Times the player actually picked this option.</summary>
    public int Picks { get; init; }
    public float PickRate => Opportunities > 0 ? (float)Picks / Opportunities : 0f;
}

/// <summary>Boss encounter statistics.</summary>
public sealed class BossEncounterStats
{
    public string EncounterId { get; init; } = "";
    public int Encounters { get; init; }
    public int Deaths { get; init; }
    public float AverageDamageTaken { get; init; }
    public float DeathRate => Encounters > 0 ? (float)Deaths / Encounters : 0f;
}

/// <summary>One Elder (Ancients event) and the option/relic breakdown beneath it.</summary>
public sealed class ElderEntry
{
    public string ElderId { get; init; } = "";
    public int Encounters { get; init; }
    public IReadOnlyList<ElderOption> Options { get; init; } = new List<ElderOption>();
    public int TotalPicks => Options.Sum(o => o.Picks);
}

/// <summary>One option (group of relics) inside an Elder encounter.</summary>
public sealed class ElderOption
{
    public string OptionTextKey { get; init; } = "";
    /// <summary>Times the player picked any relic from this option.</summary>
    public int Picks { get; init; }
    public IReadOnlyList<ElderRelicStats> Relics { get; init; } = new List<ElderRelicStats>();
}

/// <summary>One relic offered inside an Elder option.</summary>
public sealed class ElderRelicStats
{
    public string RelicId { get; init; } = "";
    public int Picks { get; init; }
    public int Wins { get; init; }
    public float WinRate => Picks > 0 ? (float)Wins / Picks : 0f;
}

/// <summary>
/// Single-run statistics for the Run History detail screen.
/// PRD §3.12.
/// </summary>
public sealed class SingleRunStatsData
{
    public string Seed { get; init; } = "";
    public string Character { get; init; } = "";
    public bool Win { get; init; }
    public int Ascension { get; init; }
    public int FloorReached { get; init; }
    public IReadOnlyDictionary<int, ActPathStats> PathStatsByAct { get; init; }
        = new Dictionary<int, ActPathStats>();
    /// <summary>Ancient relics chosen this run, in encounter order.</summary>
    public IReadOnlyList<string> AncientChoicesPicked { get; init; } = new List<string>();
    /// <summary>Boss DamageTaken samples this run, indexed by boss encounter id.</summary>
    public IReadOnlyDictionary<string, int> BossDamageTaken { get; init; }
        = new Dictionary<string, int>();
}
