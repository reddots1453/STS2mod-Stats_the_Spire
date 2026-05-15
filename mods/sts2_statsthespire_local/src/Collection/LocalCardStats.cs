using System;
using System.Collections.Generic;
using CommunityStats.Util;

namespace CommunityStats.Collection;

/// <summary>
/// Per-card aggregated statistics computed from the user's local
/// RunHistory files. Used by `CardLibraryPatch` to populate the "Mine"
/// column of the compendium card stats panel (PRD §3.2 round 5).
///
/// Counts:
///   Offered  — number of CardChoiceHistoryEntry rows that listed this card
///   Picks    — subset of Offered where wasPicked == true
///   WinsAfter — number of distinct *runs* that won AND contained this card in the deck
///   RunsWith — number of distinct *runs* that contained this card (regardless of outcome)
///
/// PickRate = Picks / Offered
/// WinRate  = WinsAfter / RunsWith
/// </summary>
public sealed class LocalCardStats
{
    public string CardId { get; init; } = "";
    public int Offered { get; init; }
    public int Picks { get; init; }
    public int RunsWith { get; init; }
    public int WinsAfter { get; init; }

    // Round 9 round 39 — three new per-card aggregations sourced from
    // PlayerMapPointHistoryEntry.{UpgradedCards, CardsRemoved, BoughtColorless}
    // + Shop-floor CardsGained, all counted at most once per run.
    public int RunsUpgraded { get; init; }
    public int RunsRemoved { get; init; }
    public int RunsBought { get; init; }

    public float PickRate    => Offered > 0 ? (float)Picks / Offered : 0f;
    public float WinRate     => RunsWith > 0 ? (float)WinsAfter / RunsWith : 0f;
    public float UpgradeRate => RunsWith > 0 ? (float)RunsUpgraded / RunsWith : 0f;
    public float RemovalRate => RunsWith > 0 ? (float)RunsRemoved / RunsWith : 0f;
    public float BuyRate     => RunsWith > 0 ? (float)RunsBought / RunsWith : 0f;
}

/// <summary>
/// Snapshot of all local card aggregations + total runs counted, returned
/// as one immutable bundle so consumers can compare against the community
/// dataset's TotalRuns side-by-side.
/// </summary>
public sealed class LocalCardStatsBundle
{
    public int TotalRuns { get; init; }
    public IReadOnlyDictionary<string, LocalCardStats> Cards { get; init; }
        = new Dictionary<string, LocalCardStats>();

    public LocalCardStats? Get(string cardId) =>
        Cards.TryGetValue(cardId, out var s) ? s : null;

    public static readonly LocalCardStatsBundle Empty = new()
    {
        TotalRuns = 0,
        Cards = new Dictionary<string, LocalCardStats>(),
    };
}
