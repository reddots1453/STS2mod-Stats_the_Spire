namespace CommunityStats.Collection;

/// <summary>
/// Accumulates combat contribution for a single source (card or relic).
/// </summary>
public class ContributionAccum
{
    public string SourceId { get; set; } = "";
    public string SourceType { get; set; } = "card"; // "card" | "relic" | "potion"
    public int TimesPlayed { get; set; }
    public int DirectDamage { get; set; }
    public int AttributedDamage { get; set; }   // indirect (poison, vuln bonus, etc.)
    public int ModifierDamage { get; set; }      // from additive/multiplicative modifiers (Strength, etc.)
    public int EffectiveBlock { get; set; }      // actual damage mitigated by block
    public int ModifierBlock { get; set; }       // from additive/multiplicative block modifiers (Dexterity, etc.)
    public int MitigatedByDebuff { get; set; }   // damage prevented by Weak/etc on enemies
    public int MitigatedByBuff { get; set; }     // damage prevented by Buffer/Intangible
    public int CardsDrawn { get; set; }
    public int EnergyGained { get; set; }
    public int HpHealed { get; set; }
    public int StarsContribution { get; set; }       // stars gained or saved (VoidForm cost reduction)
    public int MitigatedByStrReduction { get; set; } // damage prevented by reducing enemy strength
    public int SelfDamage { get; set; }              // HP lost from self-damage cards (negative defense)

    /// <summary>Parent card that generated/transformed this card (for sub-bar display).</summary>
    public string? OriginSourceId { get; set; }

    /// <summary>Damage contributed by upgrade delta (attributed to the upgrade source).</summary>
    public int UpgradeDamage { get; set; }
    public int UpgradeBlock { get; set; }

    public int TotalDamage => DirectDamage + AttributedDamage + ModifierDamage + UpgradeDamage;
    public int TotalDefense => EffectiveBlock + ModifierBlock + MitigatedByDebuff + MitigatedByBuff + MitigatedByStrReduction + UpgradeBlock - SelfDamage;

    public void MergeFrom(ContributionAccum other)
    {
        TimesPlayed += other.TimesPlayed;
        DirectDamage += other.DirectDamage;
        AttributedDamage += other.AttributedDamage;
        ModifierDamage += other.ModifierDamage;
        EffectiveBlock += other.EffectiveBlock;
        ModifierBlock += other.ModifierBlock;
        MitigatedByDebuff += other.MitigatedByDebuff;
        MitigatedByBuff += other.MitigatedByBuff;
        CardsDrawn += other.CardsDrawn;
        EnergyGained += other.EnergyGained;
        HpHealed += other.HpHealed;
        StarsContribution += other.StarsContribution;
        MitigatedByStrReduction += other.MitigatedByStrReduction;
        SelfDamage += other.SelfDamage;
        UpgradeDamage += other.UpgradeDamage;
        UpgradeBlock += other.UpgradeBlock;
        // Round 14 v5 review §12: preserve sub-bar parent tag on merge.
        if (OriginSourceId == null) OriginSourceId = other.OriginSourceId;
    }
}

/// <summary>
/// Tracks which card or relic applied each power, enabling indirect damage attribution.
/// e.g., if "Noxious Fumes" applied Poison, damage from Poison ticks is attributed back to that card.
/// Also tracks debuffs on specific creatures and block sources for FIFO attribution.
/// </summary>
public class ContributionMap
{
    public static ContributionMap Instance { get; } = new();

    // PowerModel.Id.Entry → list of (sourceId, sourceType, amount) contributions
    // Multiple sources can contribute to the same power (e.g. Inflame+Vajra both give Str).
    private readonly Dictionary<string, List<PowerSourceEntry>> _powerSources = new();

    public record PowerSource(string SourceId, string SourceType);
    public record PowerSourceEntry(string SourceId, string SourceType, int Amount);

    // Per-creature debuff tracking: (creatureHash, powerId) → source
    private readonly Dictionary<(int, string), PowerSource> _creatureDebuffSources = new();

    // Per-creature buff tracking (on player): (powerId) → list of sources
    private readonly Dictionary<string, List<PowerSourceEntry>> _playerBuffSources = new();

    // Block pool: FIFO queue of block chunks. Each chunk carries an optional
    // modifier breakdown so that when the chunk is consumed by incoming damage,
    // the consumed amount can be proportionally split between the base source
    // (→ EffectiveBlock) and each modifier source (→ ModifierBlock). This
    // matches the damage path symmetry (DirectDamage + ModifierDamage = Total)
    // AND ensures modifier contributions are only credited when the block is
    // actually used to absorb damage (not at gain time). Addresses Round 14 v5
    // Footwork/Dex potion timing issue.
    private readonly List<BlockEntry> _blockPool = new();

    public class BlockEntry
    {
        public string SourceId = "";        // base source (the card/relic that granted block)
        public string SourceType = "";
        public int OriginalTotal;            // base + sum(modifiers) at creation time
        public int OriginalBase;             // base-only portion
        public int Remaining;                // current remaining = OriginalTotal - cumulativeConsumed
        public List<(string Id, string Type, int Amount)>? Modifiers;  // original per-modifier amounts
        public int BaseConsumed;             // cumulative base portion already consumed
        public int[]? ModConsumed;           // parallel to Modifiers, cumulative per-mod consumed
    }

    /// <summary>Consumed block chunk returned by ConsumeBlock; flag says whether the slice goes to ModifierBlock vs EffectiveBlock.</summary>
    public readonly record struct ConsumedBlockSlice(string SourceId, string SourceType, int Amount, bool IsModifier);

    public void RecordPowerSource(string powerId, string sourceId, string sourceType, int amount = 0)
    {
        // Round 14 v5 review §7: defense-in-depth — reject negative contributions.
        // Negative amounts (e.g. Friendship's "Lose 2 Strength" self-cost) must not
        // enter the global source table, otherwise DistributeByPowerSources'
        // Math.Max(Amount, 1) masks them as positive shares and produces phantom
        // attribution. Existing callers (OnPowerApplied) already guard, but this
        // is a safety net for any future call site.
        if (amount < 0) return;
        if (!_powerSources.TryGetValue(powerId, out var list))
        {
            list = new List<PowerSourceEntry>();
            _powerSources[powerId] = list;
        }
        // If same source already recorded, update amount
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].SourceId == sourceId)
            {
                list[i] = new PowerSourceEntry(sourceId, sourceType, list[i].Amount + amount);
                return;
            }
        }
        list.Add(new PowerSourceEntry(sourceId, sourceType, amount));
    }

    public void RecordDebuffSource(int creatureHash, string powerId, string sourceId, string sourceType)
    {
        _creatureDebuffSources[(creatureHash, powerId)] = new PowerSource(sourceId, sourceType);
    }

    public void RecordPlayerBuffSource(string powerId, string sourceId, string sourceType, int amount = 0)
    {
        // Round 14 v5 review §7: negative-amount defense-in-depth (see RecordPowerSource).
        if (amount < 0) return;
        if (!_playerBuffSources.TryGetValue(powerId, out var list))
        {
            list = new List<PowerSourceEntry>();
            _playerBuffSources[powerId] = list;
        }
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].SourceId == sourceId)
            {
                list[i] = new PowerSourceEntry(sourceId, sourceType, list[i].Amount + amount);
                return;
            }
        }
        list.Add(new PowerSourceEntry(sourceId, sourceType, amount));
    }

    /// <summary>Clear the block FIFO pool. Call after LoseBlock to sync pool with actual block.</summary>
    public void ClearBlockPool() => _blockPool.Clear();

    public void AddBlock(string sourceId, string sourceType, int amount)
    {
        AddBlock(sourceId, sourceType, amount, null);
    }

    /// <summary>
    /// Add a block chunk with optional modifier breakdown. Modifiers are recorded
    /// as part of the chunk and split proportionally on consume, so Dex/Focus/
    /// Footwork bonuses only credit ModifierBlock when the block is actually used.
    /// </summary>
    public void AddBlock(string sourceId, string sourceType, int baseAmount,
        List<(string Id, string Type, int Amount)>? modifiers)
    {
        int modSum = 0;
        if (modifiers != null)
            foreach (var m in modifiers) modSum += Math.Max(0, m.Amount);
        int total = baseAmount + modSum;
        if (total <= 0) return;

        var entry = new BlockEntry
        {
            SourceId = sourceId,
            SourceType = sourceType,
            OriginalBase = baseAmount,
            OriginalTotal = total,
            Remaining = total,
            Modifiers = modifiers,
            ModConsumed = modifiers != null ? new int[modifiers.Count] : null,
        };
        _blockPool.Add(entry);
    }

    /// <summary>
    /// Consume block from the pool (FIFO). Each slice returned is tagged as
    /// EffectiveBlock (base) or ModifierBlock (modifier). Caller writes the
    /// slice to the corresponding contribution field.
    /// </summary>
    public List<ConsumedBlockSlice> ConsumeBlockDetailed(int blockedDamage)
    {
        var result = new List<ConsumedBlockSlice>();
        var remaining = blockedDamage;

        for (int i = 0; i < _blockPool.Count && remaining > 0; i++)
        {
            var entry = _blockPool[i];
            if (entry.Remaining <= 0) continue;

            int take = Math.Min(entry.Remaining, remaining);
            int consumedAfter = entry.OriginalTotal - entry.Remaining + take;

            // Cumulative proportional split. Using cumulative targets avoids
            // rounding drift across multiple partial consumes from the same entry.
            int newBaseCum = entry.OriginalTotal > 0
                ? (int)((long)entry.OriginalBase * consumedAfter / entry.OriginalTotal)
                : 0;
            int baseDelta = newBaseCum - entry.BaseConsumed;

            // Compute modifier deltas before committing cumulative state so we
            // can fix rounding residue in one pass.
            int[]? modDeltas = null;
            if (entry.Modifiers != null && entry.ModConsumed != null)
            {
                modDeltas = new int[entry.Modifiers.Count];
                for (int j = 0; j < entry.Modifiers.Count; j++)
                {
                    int modOrig = entry.Modifiers[j].Amount;
                    int newModCum = entry.OriginalTotal > 0
                        ? (int)((long)modOrig * consumedAfter / entry.OriginalTotal)
                        : 0;
                    modDeltas[j] = newModCum - entry.ModConsumed[j];
                }
            }

            int totalAllocated = baseDelta;
            if (modDeltas != null) foreach (var d in modDeltas) totalAllocated += d;

            // Absorb rounding residue into base so base + sum(mods) == take exactly.
            int residue = take - totalAllocated;
            if (residue != 0) baseDelta += residue;

            // Commit cumulative state
            entry.BaseConsumed += baseDelta;
            if (modDeltas != null && entry.ModConsumed != null)
                for (int j = 0; j < modDeltas.Length; j++) entry.ModConsumed[j] += modDeltas[j];

            // Emit slices
            if (baseDelta > 0)
                result.Add(new ConsumedBlockSlice(entry.SourceId, entry.SourceType, baseDelta, false));
            if (modDeltas != null && entry.Modifiers != null)
            {
                for (int j = 0; j < modDeltas.Length; j++)
                {
                    if (modDeltas[j] > 0)
                    {
                        var m = entry.Modifiers[j];
                        result.Add(new ConsumedBlockSlice(m.Id, m.Type, modDeltas[j], true));
                    }
                }
            }

            entry.Remaining -= take;
            remaining -= take;
        }

        _blockPool.RemoveAll(e => e.Remaining <= 0);
        return result;
    }

    // Round 14 v5 review §11: legacy ConsumeBlock (base-only slices) removed.
    // All callers switched to ConsumeBlockDetailed which routes modifier slices
    // to ModifierBlock correctly (Fix 4).

    /// <summary>Get all sources for a power (multi-source support).</summary>
    public IReadOnlyList<PowerSourceEntry>? GetPowerSources(string powerId)
    {
        return _powerSources.GetValueOrDefault(powerId);
    }

    /// <summary>Legacy single-source lookup (returns first source). Use GetPowerSources for multi-source.</summary>
    public PowerSource? GetPowerSource(string powerId)
    {
        var list = _powerSources.GetValueOrDefault(powerId);
        return list != null && list.Count > 0 ? new PowerSource(list[0].SourceId, list[0].SourceType) : null;
    }

    /// <summary>
    /// Distribute a total modifier amount across all sources of a power proportionally.
    /// Returns list of (sourceId, sourceType, allocatedAmount).
    /// </summary>
    public List<(string sourceId, string sourceType, int amount)> DistributeByPowerSources(
        string powerId, int totalAmount)
    {
        var result = new List<(string, string, int)>();
        var sources = _powerSources.GetValueOrDefault(powerId);
        if (sources == null || sources.Count == 0)
        {
            // No known sources — attribute to power itself
            result.Add((powerId, "power", totalAmount));
            return result;
        }
        if (sources.Count == 1)
        {
            result.Add((sources[0].SourceId, sources[0].SourceType, totalAmount));
            return result;
        }

        // Proportional distribution by recorded amount
        int totalRecorded = 0;
        foreach (var s in sources) totalRecorded += Math.Max(s.Amount, 1);

        int allocated = 0;
        for (int i = 0; i < sources.Count; i++)
        {
            int share;
            if (i == sources.Count - 1)
                share = totalAmount - allocated; // remainder to last
            else
                share = (int)((long)totalAmount * Math.Max(sources[i].Amount, 1) / totalRecorded);
            if (share > 0)
                result.Add((sources[i].SourceId, sources[i].SourceType, share));
            allocated += share;
        }
        return result;
    }

    public PowerSource? GetDebuffSource(int creatureHash, string powerId)
    {
        return _creatureDebuffSources.GetValueOrDefault((creatureHash, powerId));
    }

    // ── FIFO Debuff Layer Attribution (H1) ──────────────────
    // Tracks per-layer source for duration-based debuffs (Vulnerable, Weak).
    // When multiple cards apply the same debuff, each application records its source + duration.
    // Attribution uses FIFO: earliest layers are consumed first.

    public record DebuffLayerEntry(string SourceId, string SourceType, int Duration);

    private readonly Dictionary<(int creatureHash, string powerId), List<DebuffLayerEntry>> _debuffLayers = new();

    /// <summary>
    /// Record a debuff layer application. Called when a power is applied to a creature.
    /// </summary>
    public void RecordDebuffLayer(int creatureHash, string powerId, string sourceId, string sourceType, int duration)
    {
        var key = (creatureHash, powerId);
        if (!_debuffLayers.TryGetValue(key, out var layers))
        {
            layers = new List<DebuffLayerEntry>();
            _debuffLayers[key] = layers;
        }
        layers.Add(new DebuffLayerEntry(sourceId, sourceType, duration));
    }

    /// <summary>
    /// Get fractional attribution for a debuff's sources (FIFO-based).
    /// Returns list of (sourceId, sourceType, fraction) where fractions sum to 1.0.
    /// Falls back to single-source GetDebuffSource if no layers recorded.
    /// </summary>
    public List<(string SourceId, string SourceType, float Fraction)> GetDebuffSourceFractions(int creatureHash, string powerId)
    {
        var result = new List<(string, string, float)>();
        var key = (creatureHash, powerId);

        if (_debuffLayers.TryGetValue(key, out var layers) && layers.Count > 0)
        {
            int totalDuration = 0;
            foreach (var l in layers) totalDuration += l.Duration;
            if (totalDuration > 0)
            {
                foreach (var l in layers)
                {
                    float frac = (float)l.Duration / totalDuration;
                    result.Add((l.SourceId, l.SourceType, frac));
                }
                return result;
            }
        }

        // Fallback to single source
        var single = GetDebuffSource(creatureHash, powerId);
        if (single != null)
            result.Add((single.SourceId, single.SourceType, 1.0f));

        return result;
    }

    /// <summary>
    /// Decrement debuff layers at turn end (FIFO: earliest layers consumed first).
    /// </summary>
    public void DecrementDebuffLayers(int creatureHash, string powerId)
    {
        var key = (creatureHash, powerId);
        if (!_debuffLayers.TryGetValue(key, out var layers) || layers.Count == 0) return;

        // FIFO: decrement first layer, remove if expired
        layers[0] = layers[0] with { Duration = layers[0].Duration - 1 };
        if (layers[0].Duration <= 0)
            layers.RemoveAt(0);
    }

    public PowerSource? GetPlayerBuffSource(string powerId)
    {
        var list = _playerBuffSources.GetValueOrDefault(powerId);
        return list != null && list.Count > 0 ? new PowerSource(list[0].SourceId, list[0].SourceType) : null;
    }

    // ── Damage Modifier Context ──────────────────────────────
    // Populated by Hook.ModifyDamageInternal patch, consumed by DamageReceived patch.

    /// <summary>Per-modifier damage contributions from the most recent ModifyDamage call.</summary>
    public List<ModifierContribution> LastDamageModifiers { get; } = new();

    /// <summary>Per-modifier block contributions from the most recent ModifyBlock call.</summary>
    public List<ModifierContribution> LastBlockModifiers { get; } = new();

    public record ModifierContribution(string SourceId, string SourceType, int Amount);

    // ── Card Origin Tracking ───────────────────────────────
    // Tracks which card generated/transformed another card (for sub-bar display).

    private readonly Dictionary<int, (string originId, string originType)> _cardOriginMap = new();

    public void RecordCardOrigin(int cardHash, string originId, string originType)
    {
        _cardOriginMap[cardHash] = (originId, originType);
    }

    public (string originId, string originType)? GetCardOrigin(int cardHash)
    {
        return _cardOriginMap.TryGetValue(cardHash, out var origin) ? origin : null;
    }

    // ── Enemy Strength Reduction Tracking ──────────────────
    // Tracks player-caused strength reductions on enemies for defense attribution.

    private readonly Dictionary<int, List<StrReductionEntry>> _enemyStrReductions = new();

    public record StrReductionEntry(string SourceId, string SourceType, int Amount, bool IsTemporary);

    public void RecordStrengthReduction(int enemyHash, string sourceId, string sourceType, int amount, bool isTemporary)
    {
        if (amount <= 0) return;
        if (!_enemyStrReductions.TryGetValue(enemyHash, out var list))
        {
            list = new List<StrReductionEntry>();
            _enemyStrReductions[enemyHash] = list;
        }
        var existing = list.FindIndex(e => e.SourceId == sourceId);
        if (existing >= 0)
            list[existing] = list[existing] with { Amount = list[existing].Amount + amount };
        else
            list.Add(new StrReductionEntry(sourceId, sourceType, amount, isTemporary));
    }

    public void RevertTemporaryStrReduction(int enemyHash)
    {
        if (_enemyStrReductions.TryGetValue(enemyHash, out var list))
            list.RemoveAll(e => e.IsTemporary);
    }

    /// <summary>
    /// Subtract <paramref name="amount"/> from recorded reductions on
    /// <paramref name="enemyHash"/>, removing entries whose Amount hits zero.
    /// Called when an enemy's StrengthPower goes UP (e.g. TempStr.AfterTurnEnd
    /// revert, or some unusual buff) so stale recorded reductions don't
    /// carry into subsequent hits / turns.
    ///
    /// LIFO fallback — only used when we cannot identify which source is
    /// being reverted. <see cref="ConsumeReductionFromSource"/> is preferred
    /// (exact-source match) and is used by the TempStr revert path.
    /// </summary>
    public void ReduceRecordedStrReduction(int enemyHash, int amount)
    {
        if (amount <= 0) return;
        if (!_enemyStrReductions.TryGetValue(enemyHash, out var list)) return;
        int remaining = amount;
        for (int i = list.Count - 1; i >= 0 && remaining > 0; i--)
        {
            int take = Math.Min(list[i].Amount, remaining);
            int newAmt = list[i].Amount - take;
            remaining -= take;
            if (newAmt <= 0) list.RemoveAt(i);
            else list[i] = list[i] with { Amount = newAmt };
        }
    }

    /// <summary>
    /// Consume up to <paramref name="amount"/> from the specific
    /// <paramref name="sourceId"/>'s recorded reduction on
    /// <paramref name="enemyHash"/>, removing the entry when it hits zero.
    /// Used when a TemporaryStrengthPower reverts — we know which source
    /// applied the temp buff (TempStr.OriginModel), so we consume its entry
    /// precisely instead of relying on LIFO order.
    /// </summary>
    public void ConsumeReductionFromSource(int enemyHash, string sourceId, int amount)
    {
        if (amount <= 0 || string.IsNullOrEmpty(sourceId)) return;
        if (!_enemyStrReductions.TryGetValue(enemyHash, out var list)) return;
        int idx = list.FindIndex(e => e.SourceId == sourceId);
        if (idx < 0) return;
        int newAmt = list[idx].Amount - amount;
        if (newAmt <= 0) list.RemoveAt(idx);
        else list[idx] = list[idx] with { Amount = newAmt };
    }

    // AsyncLocal tag: set by TempStrengthRevertPatch.BeforeTempStrRevert prefix,
    // cleared by its postfix. While set, StrengthPower.SetAmount postfix with
    // delta>0 treats the change as a specific-source revert and uses
    // ConsumeReductionFromSource instead of LIFO. AsyncLocal's EC flow
    // propagates the tag through the awaited `PowerCmd.Apply<StrengthPower>`
    // chain inside AfterTurnEnd so the nested SetAmount call sees it.
    private static readonly AsyncLocal<(string SourceId, string SourceType)?> _revertingTempStrSourceAL = new();
    public (string SourceId, string SourceType)? RevertingTempStrSource
    {
        get => _revertingTempStrSourceAL.Value;
        set => _revertingTempStrSourceAL.Value = value;
    }

    public IReadOnlyList<StrReductionEntry>? GetStrReductions(int enemyHash)
        => _enemyStrReductions.GetValueOrDefault(enemyHash);

    // ── SeekingEdge Context ─────────────────────────────────
    // When SovereignBlade attacks all enemies via SeekingEdge, damage to non-primary
    // targets is attributed to SeekingEdge source instead of SovereignBlade.

    private int _seekingEdgePrimaryTargetHash;
    private bool _seekingEdgeActive;

    public bool IsSeekingEdgeActive => _seekingEdgeActive;
    public int SeekingEdgePrimaryTarget => _seekingEdgePrimaryTargetHash;

    public void SetSeekingEdgeContext(int primaryTargetHash)
    {
        _seekingEdgeActive = true;
        _seekingEdgePrimaryTargetHash = primaryTargetHash;
    }

    public void ClearSeekingEdgeContext()
    {
        _seekingEdgeActive = false;
        _seekingEdgePrimaryTargetHash = 0;
    }

    // ── Cost Reduction Source Tags (NEW-1/NEW-2) ──────────────
    // Layer 1: records which source last reduced a card's cost.
    // Only tags (no amounts) — safe even when triggered by UI display.
    // Layer 2 (in CombatHistoryPatch.AfterCardPlayStarted) computes actual savings at play time.

    private readonly Dictionary<int, (string sourceId, string sourceType)> _costReductionSourceTag = new();
    private readonly HashSet<int> _generatedAndFreedCards = new();

    public void TagCostReductionSource(int cardHash, string sourceId, string sourceType)
    {
        _costReductionSourceTag[cardHash] = (sourceId, sourceType);
    }

    public (string sourceId, string sourceType)? GetCostReductionSourceTag(int cardHash)
    {
        return _costReductionSourceTag.TryGetValue(cardHash, out var tag) ? tag : null;
    }

    public void ClearCostReductionSourceTag(int cardHash)
    {
        _costReductionSourceTag.Remove(cardHash);
    }

    public void MarkCardAsGeneratedAndFree(int cardHash)
    {
        _generatedAndFreedCards.Add(cardHash);
    }

    public bool IsCardGeneratedAndFree(int cardHash)
    {
        return _generatedAndFreedCards.Contains(cardHash);
    }

    // ── GainMaxHp Context Flag (NEW-3) ─────────────────────────
    // Set in GainMaxHp Prefix, cleared in HealingPatch when the internal Heal fires.
    // Prevents double-counting: GainMaxHp already records HpHealed, so its internal Heal is skipped.

    private bool _isFromGainMaxHp;

    public void SetGainMaxHpFlag(bool value) { _isFromGainMaxHp = value; }

    /// <summary>
    /// Returns true if current Heal is from GainMaxHp (should be suppressed). Clears flag after check.
    /// </summary>
    public bool CheckAndClearGainMaxHpFlag()
    {
        if (_isFromGainMaxHp)
        {
            _isFromGainMaxHp = false;
            return true;
        }
        return false;
    }

    // ── Pending Hand Draw Bonus ─────────────────────────────
    // Recorded by ModifyHandDraw patches, consumed after hand draw completes.

    private readonly List<(string sourceId, string sourceType, int count)> _pendingHandDrawBonus = new();

    public void RecordHandDrawBonus(string sourceId, string sourceType, int extraDraw)
    {
        if (extraDraw > 0)
            _pendingHandDrawBonus.Add((sourceId, sourceType, extraDraw));
    }

    public List<(string sourceId, string sourceType, int count)> ConsumePendingHandDrawBonus()
    {
        var result = new List<(string, string, int)>(_pendingHandDrawBonus);
        _pendingHandDrawBonus.Clear();
        return result;
    }

    // ── Osty HP Source LIFO Stack ─────────────────────────────
    // Tracks which card/relic summoned Osty HP, consumed LIFO when Osty takes damage.

    private readonly List<OstyHpEntry> _ostyHpStack = new();

    public class OstyHpEntry
    {
        public string SourceId = "";
        public string SourceType = "";
        public int Remaining;
    }

    public void PushOstyHp(string sourceId, string sourceType, int amount)
    {
        if (amount <= 0) return;
        _ostyHpStack.Add(new OstyHpEntry { SourceId = sourceId, SourceType = sourceType, Remaining = amount });
    }

    /// <summary>
    /// Consume Osty HP from the stack (LIFO) and return per-source consumption.
    /// </summary>
    public List<(string SourceId, string SourceType, int Amount)> ConsumeOstyHp(int damage)
    {
        var result = new List<(string, string, int)>();
        var remaining = damage;

        // LIFO: consume from end of list
        for (int i = _ostyHpStack.Count - 1; i >= 0 && remaining > 0; i--)
        {
            var entry = _ostyHpStack[i];
            if (entry.Remaining <= 0) continue;

            var consumed = Math.Min(entry.Remaining, remaining);
            result.Add((entry.SourceId, entry.SourceType, consumed));
            entry.Remaining -= consumed;
            remaining -= consumed;
        }

        // Cleanup empty entries
        _ostyHpStack.RemoveAll(e => e.Remaining <= 0);
        return result;
    }

    /// <summary>
    /// Returns total remaining HP in the Osty LIFO stack.
    /// </summary>
    public int GetRemainingOstyHpTotal()
    {
        int total = 0;
        foreach (var entry in _ostyHpStack) total += entry.Remaining;
        return total;
    }

    public void ClearOstyHpStack()
    {
        _ostyHpStack.Clear();
    }

    // ── Doom HP Capture ─────────────────────────────────────
    // Captures enemy HP before Doom kills them, so we can attribute damage.

    private readonly List<(int CreatureHash, int Hp)> _pendingDoomHp = new();

    public void RecordDoomTargetHp(int creatureHash, int hp)
    {
        _pendingDoomHp.Add((creatureHash, hp));
    }

    public List<(int CreatureHash, int Hp)> ConsumeDoomTargetHp()
    {
        var result = new List<(int, int)>(_pendingDoomHp);
        _pendingDoomHp.Clear();
        return result;
    }

    // Fix B: per-enemy FIFO Doom attribution stack. Replaces the shared _powerSources
    // lookup for DOOM_POWER so multi-source Doom on a single target credits each
    // application proportionally, and self-doom (BorrowedTime / Neurosurge) never
    // pollutes the attribution.
    public sealed record DoomLayer(string SourceId, string SourceType, string? OriginId, int Amount);

    private readonly Dictionary<int, List<DoomLayer>> _doomStacks = new();

    public void RecordDoomLayer(int creatureHash, string sourceId, string sourceType, int amount, string? originId = null)
    {
        if (amount <= 0) return;
        if (!_doomStacks.TryGetValue(creatureHash, out var list))
        {
            list = new List<DoomLayer>();
            _doomStacks[creatureHash] = list;
        }
        list.Add(new DoomLayer(sourceId, sourceType, originId, amount));
    }

    /// <summary>
    /// FIFO-consume doom layers for a killed enemy. Returns one entry per
    /// layer slice with the portion of `hpAtKill` attributed to that source.
    /// Remaining layers (if any) are kept — the enemy survived unused doom.
    /// </summary>
    public List<(string SourceId, string SourceType, string? OriginId, int Share)> ConsumeDoomLayers(int creatureHash, int hpAtKill)
    {
        var result = new List<(string, string, string?, int)>();
        if (hpAtKill <= 0) return result;
        if (!_doomStacks.TryGetValue(creatureHash, out var list) || list.Count == 0)
            return result;

        int remaining = hpAtKill;
        while (remaining > 0 && list.Count > 0)
        {
            var head = list[0];
            int take = Math.Min(head.Amount, remaining);
            result.Add((head.SourceId, head.SourceType, head.OriginId, take));
            remaining -= take;
            if (take >= head.Amount)
                list.RemoveAt(0);
            else
                list[0] = head with { Amount = head.Amount - take };
        }

        if (list.Count == 0)
            _doomStacks.Remove(creatureHash);
        return result;
    }

    public void ClearDoomStacks() => _doomStacks.Clear();

    // ── Orb Source Tracking ────────────────────────────────
    // Tracks which card/relic channeled each orb for indirect contribution attribution.

    private readonly Dictionary<int, OrbSource> _orbSources = new();

    public record OrbSource(string SourceId, string SourceType, string OrbType);

    public void RecordOrbSource(int orbHash, string sourceId, string sourceType, string orbType)
    {
        _orbSources[orbHash] = new OrbSource(sourceId, sourceType, orbType);
    }

    public OrbSource? GetOrbSource(int orbHash)
    {
        return _orbSources.GetValueOrDefault(orbHash);
    }

    // ── Orb Passive Trigger Count + Extra Trigger Source ────
    // Hook.ModifyOrbPassiveTriggerCount may raise the count (e.g. GoldPlatedCables
    // adds +1 to the front orb). Track per-orb trigger index so the second trigger
    // attributes to the modifying relic/power, not the orb's channeler.

    private readonly Dictionary<int, int> _orbPassiveTriggerCount = new();
    private readonly Dictionary<int, (string id, string type)> _orbExtraTriggerSource = new();

    public int IncrementOrbPassiveTriggerCount(int orbHash)
    {
        int next = _orbPassiveTriggerCount.TryGetValue(orbHash, out var cur) ? cur + 1 : 1;
        _orbPassiveTriggerCount[orbHash] = next;
        return next;
    }

    public void ResetOrbPassiveTriggerCount(int orbHash)
    {
        _orbPassiveTriggerCount[orbHash] = 0;
    }

    public void ResetAllOrbPassiveTriggerCounts()
    {
        _orbPassiveTriggerCount.Clear();
    }

    public void SetOrbExtraTriggerSource(int orbHash, string sourceId, string sourceType)
    {
        if (!string.IsNullOrEmpty(sourceId))
            _orbExtraTriggerSource[orbHash] = (sourceId, sourceType);
    }

    public void ClearOrbExtraTriggerSource(int orbHash)
    {
        _orbExtraTriggerSource.Remove(orbHash);
    }

    public (string id, string type)? GetOrbExtraTriggerSource(int orbHash)
    {
        return _orbExtraTriggerSource.TryGetValue(orbHash, out var v) ? v : null;
    }

    // ── Active Orb Context ──────────────────────────────────
    // Set by OrbPassivePatch / OrbEvokePatch prefix when an orb is about to
    // fire, read by CombatTracker.ResolveSource so damage/block/energy/draw
    // the orb produces attribute to the channeling card (or extra-trigger
    // source). First-trigger logic: during a card play, the first orb
    // trigger goes to the channeling source, subsequent triggers go to
    // the evoke/trigger card (_activeCardId).
    //
    // AsyncLocal-backed: OrbCmd.Passive / orb.Evoke are `async Task`, so
    // context must flow through `await` continuations inside the orb's
    // body. Crucially, a bare static field would LEAK after the async flow
    // returns — subsequent unrelated power/relic hooks in the same turn
    // (PoisonPower tick, StoneCalendar end-of-turn, etc.) would inherit
    // the stale orb source and misattribute. AsyncLocal's copy-on-write
    // ExecutionContext semantics isolate the value to the orb's own async
    // subtree: inner set propagates through awaits within the orb flow,
    // but the CALLER's context (where the orb was awaited from) keeps its
    // pre-orb value (usually null). No postfix, Task wrapper, or "clear
    // on SetRelic/SetPower entry" hack needed — the runtime unsets it
    // automatically at flow boundaries.

    private static readonly AsyncLocal<(string sourceId, string sourceType, string orbType)?> _activeOrbContextAL = new();
    private static readonly AsyncLocal<(string sourceId, string sourceType, int amount)?> _pendingOrbFocusContribAL = new();

    private (string sourceId, string sourceType, string orbType)? _activeOrbContext
    {
        get => _activeOrbContextAL.Value;
        set => _activeOrbContextAL.Value = value;
    }

    private (string sourceId, string sourceType, int amount)? _pendingOrbFocusContrib
    {
        get => _pendingOrbFocusContribAL.Value;
        set => _pendingOrbFocusContribAL.Value = value;
    }

    /// <summary>
    /// True after the first orb trigger during a card play has been consumed.
    /// Reset at each card play start. When true, orb context is NOT set so that
    /// _activeCardId (the evoke card) takes priority in ResolveSource.
    ///
    /// NOT AsyncLocal: this flag tracks card-play-scoped state (set/reset by
    /// CombatTracker, read by orb prefixes which share the same logical
    /// card-play flow). Plain field is correct here.
    /// </summary>
    private bool _orbFirstTriggerUsed;

    public bool HasActiveOrbContext => _activeOrbContext != null;
    public (string sourceId, string sourceType, string orbType)? ActiveOrbContext => _activeOrbContext;
    public (string sourceId, string sourceType, int amount)? PendingOrbFocusContrib => _pendingOrbFocusContrib;
    public bool OrbFirstTriggerUsed => _orbFirstTriggerUsed;

    public void SetActiveOrbContext(string sourceId, string sourceType, string orbType)
    {
        _activeOrbContext = (sourceId, sourceType, orbType);
    }

    public void MarkOrbFirstTriggerUsed()
    {
        _orbFirstTriggerUsed = true;
    }

    public void ResetOrbFirstTrigger()
    {
        _orbFirstTriggerUsed = false;
    }

    public void SetPendingOrbFocusContrib(string sourceId, string sourceType, int amount)
    {
        if (amount > 0)
            _pendingOrbFocusContrib = (sourceId, sourceType, amount);
    }

    public void ClearActiveOrbContext()
    {
        _activeOrbContext = null;
        _pendingOrbFocusContrib = null;
    }

    // ── Upgrade Delta Tracking ─────────────────────────────
    // Tracks damage/block upgrade delta per card instance, attributed to the upgrade source.

    private readonly Dictionary<int, UpgradeDelta> _upgradeDeltaMap = new();

    // Fix D: carry the upgrader's origin so a potion-generated Armaments upgrading
    // Strike credits (ARMAMENTS, SKILL_POTION) distinctly from a deck-native Armaments
    // upgrading Strike — otherwise both collapse into the same top-level ARMAMENTS bucket.
    public record UpgradeDelta(int DamageDelta, int BlockDelta, string SourceId, string SourceType, string? UpgraderOrigin);

    public void RecordUpgradeDelta(int cardHash, int damageDelta, int blockDelta, string sourceId, string sourceType, string? upgraderOrigin = null)
    {
        _upgradeDeltaMap[cardHash] = new UpgradeDelta(damageDelta, blockDelta, sourceId, sourceType, upgraderOrigin);
    }

    public UpgradeDelta? GetUpgradeDelta(int cardHash)
    {
        return _upgradeDeltaMap.GetValueOrDefault(cardHash);
    }

    // ── Enemy base damage / Str tracking (for str-reduction formula) ──
    // Set by EnemyDamageIntentPatch Prefix on Hook.ModifyDamage when dealer is
    // not player. PendingEnemyCurrentStr is the dealer's StrengthPower.Amount
    // at hit time — already includes any OUR recorded reductions. Used to
    // compute "damage without our reductions" = base + currentStr + recorded.
    public int PendingEnemyBaseDamage { get; set; }
    public int PendingEnemyCurrentStr { get; set; }
    public int PendingEnemyHitCount { get; set; } = 1;

    public void Clear()
    {
        _powerSources.Clear();
        _creatureDebuffSources.Clear();
        _debuffLayers.Clear();
        _playerBuffSources.Clear();
        _blockPool.Clear();
        LastDamageModifiers.Clear();
        LastBlockModifiers.Clear();
        _cardOriginMap.Clear();
        _upgradeDeltaMap.Clear();
        _enemyStrReductions.Clear();
        PendingEnemyBaseDamage = 0;
        PendingEnemyCurrentStr = 0;
        PendingEnemyHitCount = 1;
        _seekingEdgeActive = false;
        _seekingEdgePrimaryTargetHash = 0;
        _costReductionSourceTag.Clear();
        _generatedAndFreedCards.Clear();
        _isFromGainMaxHp = false;
        _pendingHandDrawBonus.Clear();
        _ostyHpStack.Clear();
        _pendingDoomHp.Clear();
        _doomStacks.Clear();
        _orbSources.Clear();
        _orbPassiveTriggerCount.Clear();
        _orbExtraTriggerSource.Clear();
        _activeOrbContext = null;
        _pendingOrbFocusContrib = null;
        _orbFirstTriggerUsed = false;
    }
}
