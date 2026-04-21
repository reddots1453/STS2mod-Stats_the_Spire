using CommunityStats.Api;

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
    /// </summary>
    public void HydrateRunTotals(IReadOnlyDictionary<string, ContributionAccum> totals)
    {
        _runTotals.Clear();
        foreach (var (k, v) in totals) _runTotals[k] = v;
    }

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

    // ── Export for Upload ────────────────────────────────────

    public List<EncounterUpload> BuildEncounterUploads()
    {
        return _encounters.Select(e => new EncounterUpload
        {
            EncounterId = e.EncounterId,
            EncounterType = e.EncounterType,
            DamageTaken = Math.Max(0, e.DamageTaken),
            TurnsTaken = Math.Clamp(e.TurnsTaken, 0, 999),
            PlayerDied = e.PlayerDied,
            Floor = e.Floor
        }).ToList();
    }

    // Round 14 v5: server-side ContributionUpload now accepts the full set
    // of source_types emitted by CombatTracker.ResolveSource. Previously this
    // filter dropped all "power" entries (Round 14 v5 DemonForm / FeelNoPain /
    // Juggernaut / Enrage / ... hook attributions), causing massive data loss.
    // Server migration 005 widened the column to VARCHAR(16) and Pydantic
    // clamps unknown types to "untracked" rather than rejecting the payload.
    //
    // Whitelisted types (matches server models.py clamp_source_type):
    //   card / relic / potion / power / untracked / orb / event / rest /
    //   merchant / floor_regen
    //
    // Kept as a client-side sanity guard so obviously bogus entries don't
    // hit the network.
    private static bool IsUploadableSourceType(string t)
        => !string.IsNullOrEmpty(t);

    public List<ContributionUpload> BuildContributionUploads()
    {
        var result = new List<ContributionUpload>();

        // Per-encounter contributions
        foreach (var (encounterId, contributions) in _perEncounter)
        {
            foreach (var (_, accum) in contributions)
            {
                if (!IsUploadableSourceType(accum.SourceType)) continue;
                result.Add(ToUpload(accum, encounterId));
            }
        }

        // Run-level totals (encounterId = "" means aggregate)
        foreach (var (_, accum) in _runTotals)
        {
            if (!IsUploadableSourceType(accum.SourceType)) continue;
            result.Add(ToUpload(accum, ""));
        }

        return result;
    }

    private static ContributionUpload ToUpload(ContributionAccum accum, string encounterId)
    {
        return new ContributionUpload
        {
            SourceId = accum.SourceId,
            SourceType = accum.SourceType,
            EncounterId = encounterId,
            TimesPlayed = accum.TimesPlayed,
            DirectDamage = accum.DirectDamage,
            AttributedDamage = accum.AttributedDamage,
            EffectiveBlock = accum.EffectiveBlock,
            MitigatedByDebuff = accum.MitigatedByDebuff,
            MitigatedByBuff = accum.MitigatedByBuff,
            CardsDrawn = accum.CardsDrawn,
            EnergyGained = accum.EnergyGained,
            HpHealed = accum.HpHealed,
            StarsContribution = accum.StarsContribution,
            MitigatedByStrReduction = accum.MitigatedByStrReduction,
            ModifierDamage = accum.ModifierDamage,
            ModifierBlock = accum.ModifierBlock,
            SelfDamage = accum.SelfDamage,
            UpgradeDamage = accum.UpgradeDamage,
            UpgradeBlock = accum.UpgradeBlock,
            OriginSourceId = accum.OriginSourceId
        };
    }
}
