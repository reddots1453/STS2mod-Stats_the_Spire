namespace CommunityStats.Collection;

/// <summary>
/// Run-level contribution aggregator. Accumulates data across all combats in a run.
/// At run end, provides data for upload payload.
/// </summary>
public sealed class RunContributionAggregator
{
    public static RunContributionAggregator Instance { get; } = new();

    // sourceId → run-total contributions
    private readonly Dictionary<string, ContributionAccum> _runTotals = new();

    // Per-encounter records
    private readonly List<EncounterRecord> _encounters = new();

    // Per-encounter contribution snapshots (encounterId → sourceId → ContributionAccum)
    private readonly List<(string EncounterId, Dictionary<string, ContributionAccum> Contributions)> _perEncounter = new();

    public record EncounterRecord(
        string EncounterId,
        string EncounterType,
        int Floor,
        int DamageTaken,
        int TurnsTaken,
        bool PlayerDied);

    public IReadOnlyDictionary<string, ContributionAccum> RunTotals => _runTotals;
    public IReadOnlyList<EncounterRecord> Encounters => _encounters;

    /// <summary>Total turns taken across every completed combat in this run.</summary>
    public int TotalRunTurns
    {
        get
        {
            int sum = 0;
            foreach (var e in _encounters) sum += e.TurnsTaken;
            return sum;
        }
    }

    /// <summary>
    /// Sum of all damage-dealing contributions across run totals.
    /// Direct + Modifier + Attributed + Upgrade — mirrors the bar-chart's
    /// "damage dealt" section so the DPS row matches what the user sees.
    /// </summary>
    public int TotalRunDamage
    {
        get
        {
            int sum = 0;
            foreach (var a in _runTotals.Values)
                sum += a.DirectDamage + a.ModifierDamage + a.AttributedDamage + a.UpgradeDamage;
            return sum;
        }
    }

    // ── Lifecycle ────────────────────────────────────────────

    public void Reset()
    {
        _runTotals.Clear();
        _encounters.Clear();
        _perEncounter.Clear();
    }

    /// <summary>
    /// Restore run totals from a save+quit snapshot. Round 8 §3.6.1.
    /// Replaces (not merges) the in-memory totals with the supplied dict.
    /// Does nothing when totals is empty — an empty snapshot should never
    /// wipe data that is already in memory (defense against corrupted or
    /// mid-first-combat _live.json files).
    /// </summary>
    public void HydrateRunTotals(IReadOnlyDictionary<string, ContributionAccum> totals)
    {
        if (totals.Count == 0) return;
        _runTotals.Clear();
        foreach (var (k, v) in totals) _runTotals[k] = v;
    }

    /// <summary>
    /// Restore per-encounter records from a save+quit snapshot.
    /// Without this, TotalRunTurns is 0 after SL → DPS shows "—" or
    /// inflates wildly after the first post-load combat.
    /// </summary>
    public void HydrateEncounters(IReadOnlyList<EncounterRecord> encounters)
    {
        _encounters.Clear();
        _encounters.AddRange(encounters);
    }

    /// <summary>
    /// Merge disk-reassembled totals into in-memory totals. For each
    /// contribution key, takes the MAX of each numeric field so that
    /// missing-in-memory fights are filled in without overwriting
    /// live data that may have progressed further.
    /// </summary>
    public void MergeMaxFrom(IReadOnlyDictionary<string, ContributionAccum> diskTotals)
    {
        foreach (var (key, disk) in diskTotals)
        {
            if (!_runTotals.TryGetValue(key, out var mem))
            {
                _runTotals[key] = Clone(disk);
                continue;
            }
            mem.TimesPlayed          = Math.Max(mem.TimesPlayed, disk.TimesPlayed);
            mem.DirectDamage         = Math.Max(mem.DirectDamage, disk.DirectDamage);
            mem.AttributedDamage     = Math.Max(mem.AttributedDamage, disk.AttributedDamage);
            mem.ModifierDamage       = Math.Max(mem.ModifierDamage, disk.ModifierDamage);
            mem.UpgradeDamage        = Math.Max(mem.UpgradeDamage, disk.UpgradeDamage);
            mem.EffectiveBlock       = Math.Max(mem.EffectiveBlock, disk.EffectiveBlock);
            mem.ModifierBlock        = Math.Max(mem.ModifierBlock, disk.ModifierBlock);
            mem.UpgradeBlock         = Math.Max(mem.UpgradeBlock, disk.UpgradeBlock);
            mem.MitigatedByDebuff    = Math.Max(mem.MitigatedByDebuff, disk.MitigatedByDebuff);
            mem.MitigatedByBuff      = Math.Max(mem.MitigatedByBuff, disk.MitigatedByBuff);
            mem.MitigatedByStrReduction = Math.Max(mem.MitigatedByStrReduction, disk.MitigatedByStrReduction);
            mem.SelfDamage           = Math.Max(mem.SelfDamage, disk.SelfDamage);
            mem.CardsDrawn           = Math.Max(mem.CardsDrawn, disk.CardsDrawn);
            mem.EnergyGained         = Math.Max(mem.EnergyGained, disk.EnergyGained);
            mem.HpHealed             = Math.Max(mem.HpHealed, disk.HpHealed);
            mem.StarsContribution    = Math.Max(mem.StarsContribution, disk.StarsContribution);
        }
    }

    private static ContributionAccum Clone(ContributionAccum src) => new()
    {
        SourceId                = src.SourceId,
        SourceType              = src.SourceType,
        TimesPlayed             = src.TimesPlayed,
        DirectDamage            = src.DirectDamage,
        AttributedDamage        = src.AttributedDamage,
        ModifierDamage          = src.ModifierDamage,
        UpgradeDamage           = src.UpgradeDamage,
        EffectiveBlock          = src.EffectiveBlock,
        ModifierBlock           = src.ModifierBlock,
        UpgradeBlock            = src.UpgradeBlock,
        MitigatedByDebuff       = src.MitigatedByDebuff,
        MitigatedByBuff         = src.MitigatedByBuff,
        MitigatedByStrReduction = src.MitigatedByStrReduction,
        SelfDamage              = src.SelfDamage,
        CardsDrawn              = src.CardsDrawn,
        EnergyGained            = src.EnergyGained,
        HpHealed                = src.HpHealed,
        StarsContribution       = src.StarsContribution,
        OriginSourceId          = src.OriginSourceId,
    };

    /// <summary>
    /// Adds healing directly to run totals (for healing that occurs outside combat,
    /// e.g. BurningBlood.AfterCombatVictory, rest site, events).
    /// </summary>
    public void AddHealing(string sourceId, string sourceType, int amount)
    {
        if (!_runTotals.TryGetValue(sourceId, out var accum))
        {
            accum = new ContributionAccum { SourceId = sourceId, SourceType = sourceType };
            _runTotals[sourceId] = accum;
        }
        accum.HpHealed += amount;
    }

    /// <summary>
    /// Called by CombatTracker at combat end. Merges combat data into run totals.
    /// </summary>
    public void AddCombat(
        string encounterId, string encounterType, int floor,
        int damageTaken, int turnsTaken, bool playerDied,
        Dictionary<string, ContributionAccum> combatData)
    {
        _encounters.Add(new EncounterRecord(
            encounterId, encounterType, floor, damageTaken, turnsTaken, playerDied));

        // Deep copy for per-encounter snapshot
        var snapshot = new Dictionary<string, ContributionAccum>();
        foreach (var (key, src) in combatData)
        {
            var copy = new ContributionAccum
            {
                SourceId = src.SourceId,
                SourceType = src.SourceType,
                TimesPlayed = src.TimesPlayed,
                DirectDamage = src.DirectDamage,
                AttributedDamage = src.AttributedDamage,
                ModifierDamage = src.ModifierDamage,
                EffectiveBlock = src.EffectiveBlock,
                ModifierBlock = src.ModifierBlock,
                MitigatedByDebuff = src.MitigatedByDebuff,
                MitigatedByBuff = src.MitigatedByBuff,
                MitigatedByStrReduction = src.MitigatedByStrReduction,
                CardsDrawn = src.CardsDrawn,
                EnergyGained = src.EnergyGained,
                HpHealed = src.HpHealed,
                StarsContribution = src.StarsContribution,
                OriginSourceId = src.OriginSourceId,
                SelfDamage = src.SelfDamage,
                UpgradeDamage = src.UpgradeDamage,
                UpgradeBlock = src.UpgradeBlock
            };
            snapshot[key] = copy;
        }
        _perEncounter.Add((encounterId, snapshot));

        // Merge into run totals
        foreach (var (sourceId, accum) in combatData)
        {
            if (!_runTotals.TryGetValue(sourceId, out var runAccum))
            {
                runAccum = new ContributionAccum
                {
                    SourceId = accum.SourceId,
                    SourceType = accum.SourceType
                };
                _runTotals[sourceId] = runAccum;
            }
            runAccum.MergeFrom(accum);
        }
    }

}
