using CommunityStats.Collection;
using CommunityStats.Util;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.ValueProps;
using MegaCrit.Sts2.Core.Nodes.CommonUi;

namespace CommunityStats.Patches;

// ═══════════════════════════════════════════════════════════
// Core combat history hooks
// ═══════════════════════════════════════════════════════════

[HarmonyPatch]
public static class CombatHistoryPatch
{
    [HarmonyPatch(typeof(CombatHistory), nameof(CombatHistory.CardPlayStarted))]
    [HarmonyPostfix]
    public static void AfterCardPlayStarted(CombatState combatState, CardPlay cardPlay)
    {
        Safe.Run(() =>
        {
            var card = cardPlay.Card;
            var cardId = card?.Id.Entry;
            if (cardId != null)
                CombatTracker.Instance.OnCardPlayStarted(cardId, card!.GetHashCode());

            // NEW-1/NEW-2: Centralized cost savings attribution at play time
            if (card != null)
            {
                int canonicalEnergy = card.EnergyCost.Canonical;
                int canonicalStars = card.BaseStarCost;
                int energySpent = cardPlay.Resources.EnergySpent;
                int starsSpent = cardPlay.Resources.StarsSpent;
                bool costsX = card.EnergyCost.CostsX;

                CombatTracker.Instance.AttributeCostSavings(
                    card.GetHashCode(), canonicalEnergy, energySpent,
                    canonicalStars, starsSpent, costsX);
            }
        });
    }

    [HarmonyPatch(typeof(CombatHistory), nameof(CombatHistory.CardPlayFinished))]
    [HarmonyPostfix]
    public static void AfterCardPlayFinished(CombatState combatState, CardPlay cardPlay)
    {
        Safe.Run(() =>
        {
            CombatTracker.Instance.OnCardPlayFinished();
            // Round 9 round 33: real-time refresh always fires regardless of
            // the ContributionPanel toggle (which now only gates auto-open
            // after combat). The handler in ContributionPanel still bails
            // when the panel isn't visible, so cost is negligible.
            CombatTracker.Instance.NotifyCombatDataUpdated();
        });
    }

    // Sync block pool when block is naturally cleared at turn start.
    // Without this, _blockPool retains stale entries from previous turns.
    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterBlockCleared))]
    [HarmonyPrefix]
    public static void BeforeBlockCleared(Creature creature)
    {
        Safe.Run(() =>
        {
            if (creature.IsPlayer)
                ContributionMap.Instance.ClearBlockPool();
        });
    }

    [HarmonyPatch(typeof(CombatHistory), nameof(CombatHistory.DamageReceived))]
    [HarmonyPostfix]
    public static void AfterDamageReceived(CombatState combatState,
        Creature receiver, Creature? dealer, DamageResult result, CardModel? cardSource)
    {
        Safe.Run(() =>
        {
            // Mark this result as processed so KillingBlowPatcher won't double-count
            CombatTracker.Instance.MarkDamageResultProcessed(
                System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(result));

            var isPlayerReceiver = receiver.IsPlayer;
            var cardSourceId = cardSource?.Id.Entry;

            // Detect Osty as dealer or receiver
            bool isOstyDealer = dealer != null && dealer.Monster is Osty;
            bool isOstyReceiver = receiver.Monster is Osty;

            // Skip damage attribution for enemies in an invincible phase
            // (ShowsInfiniteHp flag) — see CombatTracker.OnDamageDealt for
            // rationale. Still pass through so encounter tracking / damage
            // dedup run normally; only the enemy-side damage ledger is
            // suppressed.
            bool invincible = !isPlayerReceiver && receiver.ShowsInfiniteHp;

            CombatTracker.Instance.OnDamageDealt(
                result.TotalDamage,
                result.BlockedDamage,
                cardSourceId,
                isPlayerReceiver,
                receiver.GetHashCode(),
                dealer?.GetHashCode() ?? 0,
                isOstyDealer: isOstyDealer,
                isOstyReceiver: isOstyReceiver,
                skipEnemyDamageAttribution: invincible);

            if (isPlayerReceiver && result.WasTargetKilled)
                CombatTracker.Instance.OnPlayerDied();

            // Defense attribution: Weak on enemy attacker reduces damage to player
            if (isPlayerReceiver && dealer != null && result.TotalDamage > 0)
            {
                var weakPower = dealer.GetPower<WeakPower>();
                if (weakPower != null)
                {
                    // Fix-2: Compute actual weak multiplier including PaperKrane/Debilitate
                    decimal weakMult = 0.75m;
                    if (receiver?.Player != null)
                    {
                        var krane = receiver.Player.GetRelic<PaperKrane>();
                        if (krane != null) weakMult -= 0.15m;
                    }
                    var debilitate = dealer.GetPower<DebilitatePower>();
                    if (debilitate != null)
                        weakMult = 1m - (1m - weakMult) * 2m; // doubles the reduction
                    // Floor at 0.1 to avoid division issues
                    if (weakMult < 0.1m) weakMult = 0.1m;

                    CombatTracker.Instance.OnWeakMitigation(
                        result.TotalDamage, dealer.GetHashCode(), (float)weakMult);
                }

                // Colossus mitigation is now tracked in EnemyDamageIntentPatch.AfterModifyDamage_Enemy
                // (at ModifyDamage stage, before block consumption)
            }

            // Round 9 round 53: fire real-time UI refresh when the player
            // takes damage. Without this, out-of-turn enemy attacks (intent
            // fires between turns or mid-action) updated the tracker's block
            // / mitigation data but the F8 panel stayed stale until the next
            // card play. Only fire on player receiver to avoid spamming the
            // event for every enemy-damaged-enemy hit.
            if (isPlayerReceiver)
            {
                CombatTracker.Instance.NotifyCombatDataUpdated();
            }
        });
    }

    [HarmonyPatch(typeof(CombatHistory), nameof(CombatHistory.BlockGained))]
    [HarmonyPostfix]
    public static void AfterBlockGained(CombatState combatState,
        Creature receiver, int amount, ValueProp props, CardPlay? cardPlay)
    {
        Safe.Run(() =>
        {
            if (!receiver.IsPlayer) return;
            var cardPlayId = cardPlay?.Card?.Id.Entry;
            CombatTracker.Instance.OnBlockGained(amount, cardPlayId);

            // Round 9 round 33: block gained outside a card play (relic
            // BeforeTurnEnd / AfterBlockCleared, power tick, etc.) wasn't
            // triggering the F8 panel auto-refresh. CardPlayFinished already
            // covers the in-card path, so only fire here when cardPlay is null
            // to avoid double-refreshing during normal card plays.
            if (cardPlay == null)
            {
                CombatTracker.Instance.NotifyCombatDataUpdated();
            }
        });
    }

    [HarmonyPatch(typeof(CombatHistory), nameof(CombatHistory.PowerReceived))]
    [HarmonyPostfix]
    public static void AfterPowerReceived(CombatState combatState,
        PowerModel power, decimal amount, Creature? applier)
    {
        Safe.Run(() =>
        {
            var powerId = power?.Id.Entry;
            if (powerId == null) return;
            CombatTracker.Instance.OnPowerSourceRecorded(powerId);
        });
    }

    [HarmonyPatch(typeof(CombatHistory), nameof(CombatHistory.CardDrawn))]
    [HarmonyPostfix]
    public static void AfterCardDrawn(CombatState combatState, CardModel card, bool fromHandDraw)
    {
        Safe.Run(() => CombatTracker.Instance.OnCardDrawn(fromHandDraw));
    }
}

// ═══════════════════════════════════════════════════════════
// Module B: Power indirect effects — set/clear power source context
// Patches PowerModel hook methods so indirect damage/block from powers
// (RagePower, FlameBarrier, FeelNoPain, Plating, Inferno, Juggernaut, Grapple)
// is attributed back to the card that applied the power.
// ═══════════════════════════════════════════════════════════

[HarmonyPatch]
public static class PowerApplyPatch
{
    [HarmonyPatch(typeof(PowerModel), nameof(PowerModel.ApplyInternal))]
    [HarmonyPostfix]
    public static void AfterApplyInternal(PowerModel __instance, Creature owner)
    {
        Safe.Run(() =>
        {
            var powerId = __instance.Id.Entry;
            if (string.IsNullOrEmpty(powerId)) return;

            int creatureHash = owner.GetHashCode();
            bool isPlayerTarget = owner.IsPlayer;

            CombatTracker.Instance.OnPowerApplied(powerId, __instance.Amount, creatureHash, isPlayerTarget);
        });
    }
}

/// <summary>
/// Manual RelicHookContext patcher. Sets/clears _activeRelicId around relic hook methods
/// so indirect effects (healing, damage, block, energy, draw) are attributed to the relic.
/// For hooks that fire during card play (AfterCardPlayed), _activeCardId takes priority
/// in ResolveSource, so relic context only matters for hooks outside card play.
/// </summary>
public static class RelicHookContextPatcher
{
    // P2-3: Relics whose hook draws multiple cards in a single invocation.
    // The pending draw source must remain sticky for N draws instead of being
    // consumed on the first draw only. Maps relic ID → expected draw count.
    private static readonly Dictionary<string, int> _relicMultiDrawCounts = new()
    {
        { "CENTENNIAL_PUZZLE", 3 }, // draws 3 cards when damaged
    };

    public static void SetRelicContext(RelicModel __instance)
    {
        Safe.Run(() =>
        {
            var relicId = __instance.Id.Entry;
            CombatTracker.Instance.SetActiveRelic(relicId);
            // Pending draw source for async relic hooks (CharonsAshes draws via GamePiece etc.)
            if (_relicMultiDrawCounts.TryGetValue(relicId, out var nCount))
            {
                CombatTracker.Instance.SetPendingDrawSource(relicId, "relic", nCount);
            }
            else
            {
                CombatTracker.Instance.SetPendingDrawSource(relicId, "relic");
            }
        });
    }

    public static void ClearRelicContext()
    {
        Safe.Run(() => CombatTracker.Instance.ClearActiveRelic());
        // DON'T clear pending draw source — consumed by OnCardDrawn
    }

    public static void PatchAll(Harmony harmony)
    {
        var prefix = new HarmonyMethod(AccessTools.Method(typeof(RelicHookContextPatcher), nameof(SetRelicContext)));
        var postfix = new HarmonyMethod(AccessTools.Method(typeof(RelicHookContextPatcher), nameof(ClearRelicContext)));

        // ── Healing relics ──
        TryPatch(harmony, typeof(BurningBlood), "AfterCombatVictory", prefix, postfix);
        TryPatch(harmony, typeof(BlackBlood), "AfterCombatVictory", prefix, postfix);
        TryPatch(harmony, typeof(MeatOnTheBone), "AfterCombatVictoryEarly", prefix, postfix);
        TryPatch(harmony, typeof(DemonTongue), "AfterDamageReceived", prefix, postfix);

        // ── Damage relics (outside card play) ──
        TryPatch(harmony, typeof(ScreamingFlagon), "BeforeTurnEnd", prefix, postfix);
        TryPatch(harmony, typeof(StoneCalendar), "BeforeTurnEnd", prefix, postfix);
        TryPatch(harmony, typeof(ParryingShield), "AfterTurnEnd", prefix, postfix);
        TryPatch(harmony, typeof(CharonsAshes), "AfterCardExhausted", prefix, postfix);
        TryPatch(harmony, typeof(ForgottenSoul), "AfterCardExhausted", prefix, postfix);
        TryPatch(harmony, typeof(FestivePopper), "AfterPlayerTurnStart", prefix, postfix);
        TryPatch(harmony, typeof(MercuryHourglass), "AfterPlayerTurnStart", prefix, postfix);
        TryPatch(harmony, typeof(MrStruggles), "AfterPlayerTurnStart", prefix, postfix);
        TryPatch(harmony, typeof(Metronome), "AfterOrbChanneled", prefix, postfix);
        TryPatch(harmony, typeof(Tingsha), "AfterCardDiscarded", prefix, postfix);

        // ── Damage relics (during card play — relic context used for power source recording) ──
        TryPatch(harmony, typeof(Kusarigama), "AfterCardPlayed", prefix, postfix);
        TryPatch(harmony, typeof(LetterOpener), "AfterCardPlayed", prefix, postfix);
        TryPatch(harmony, typeof(LostWisp), "AfterCardPlayed", prefix, postfix);

        // ── Block relics (outside card play) ──
        TryPatch(harmony, typeof(CloakClasp), "BeforeTurnEnd", prefix, postfix);
        TryPatch(harmony, typeof(FakeOrichalcum), "BeforeTurnEnd", prefix, postfix);
        TryPatch(harmony, typeof(Orichalcum), "BeforeTurnEnd", prefix, postfix);
        TryPatch(harmony, typeof(RippleBasin), "BeforeTurnEnd", prefix, postfix);
        TryPatch(harmony, typeof(ToughBandages), "AfterCardDiscarded", prefix, postfix);
        TryPatch(harmony, typeof(IntimidatingHelmet), "BeforeCardPlayed", prefix, postfix);
        TryPatch(harmony, typeof(HornCleat), "AfterBlockCleared", prefix, postfix);
        TryPatch(harmony, typeof(CaptainsWheel), "AfterBlockCleared", prefix, postfix);
        TryPatch(harmony, typeof(GalacticDust), "AfterStarsSpent", prefix, postfix);

        // ── Block relics (during card play) ──
        TryPatch(harmony, typeof(DaughterOfTheWind), "AfterCardPlayed", prefix, postfix);
        TryPatch(harmony, typeof(OrnamentalFan), "AfterCardPlayed", prefix, postfix);
        TryPatch(harmony, typeof(Permafrost), "AfterCardPlayed", prefix, postfix);
        TryPatch(harmony, typeof(TuningFork), "AfterCardPlayed", prefix, postfix);

        // ── Energy relics ──
        TryPatch(harmony, typeof(ArtOfWar), "AfterEnergyReset", prefix, postfix);
        TryPatch(harmony, typeof(IvoryTile), "AfterCardPlayed", prefix, postfix);
        TryPatch(harmony, typeof(Nunchaku), "AfterCardPlayed", prefix, postfix);
        TryPatch(harmony, typeof(PaelsTears), "AfterSideTurnStart", prefix, postfix);
        TryPatch(harmony, typeof(Candelabra), "AfterSideTurnStart", prefix, postfix);
        TryPatch(harmony, typeof(Chandelier), "AfterSideTurnStart", prefix, postfix);
        TryPatch(harmony, typeof(Lantern), "AfterSideTurnStart", prefix, postfix);
        TryPatch(harmony, typeof(HappyFlower), "AfterSideTurnStart", prefix, postfix);
        TryPatch(harmony, typeof(FakeHappyFlower), "AfterSideTurnStart", prefix, postfix);
        TryPatch(harmony, typeof(GremlinHorn), "AfterDeath", prefix, postfix);

        // ── Draw relics ──
        TryPatch(harmony, typeof(CentennialPuzzle), "AfterDamageReceived", prefix, postfix);
        TryPatch(harmony, typeof(GamePiece), "AfterCardPlayed", prefix, postfix);
        TryPatch(harmony, typeof(IronClub), "AfterCardPlayed", prefix, postfix);
        TryPatch(harmony, typeof(JossPaper), "AfterCardExhausted", prefix, postfix);
        // Pendulum's current game behavior: `AfterPlayerTurnStart` every N
        // turns, draws a card when counter wraps to 0. The old `AfterShuffle`
        // hook doesn't exist on this Pendulum override at all — patch would
        // silently bind to nothing and the draw attributed to UNTRACKED.
        TryPatch(harmony, typeof(Pendulum), "AfterPlayerTurnStart", prefix, postfix);
        TryPatch(harmony, typeof(BlessedAntler), "BeforeHandDraw", prefix, postfix);

        // ── Power application relics (records relic as power source) ──
        TryPatch(harmony, typeof(HandDrill), "AfterDamageGiven", prefix, postfix);
        TryPatch(harmony, typeof(Kunai), "AfterCardPlayed", prefix, postfix);
        TryPatch(harmony, typeof(Shuriken), "AfterCardPlayed", prefix, postfix);
        TryPatch(harmony, typeof(RainbowRing), "AfterCardPlayed", prefix, postfix);
        TryPatch(harmony, typeof(RedSkull), "AfterCurrentHpChanged", prefix, postfix);
        TryPatch(harmony, typeof(Akabeko), "AfterSideTurnStart", prefix, postfix);
        TryPatch(harmony, typeof(BagOfMarbles), "BeforeSideTurnStart", prefix, postfix);
        TryPatch(harmony, typeof(Brimstone), "AfterSideTurnStart", prefix, postfix);
        TryPatch(harmony, typeof(RedMask), "BeforeSideTurnStart", prefix, postfix);
        TryPatch(harmony, typeof(SlingOfCourage), "AfterRoomEntered", prefix, postfix);

        // ── Stars relic ──
        TryPatch(harmony, typeof(LunarPastry), "AfterTurnEnd", prefix, postfix);

        // ── Orb relic ──
        TryPatch(harmony, typeof(EmotionChip), "AfterPlayerTurnStart", prefix, postfix);

        // ── Food relics: +MaxHp on pickup (Catalog §14 Gap #4) ──
        // These trigger CreatureCmd.GainMaxHp inside AfterObtained, which routes to
        // MaxHpGainPatch. Setting relic context here lets the source attribute correctly.
        TryPatch(harmony, typeof(Pear), "AfterObtained", prefix, postfix);
        TryPatch(harmony, typeof(Strawberry), "AfterObtained", prefix, postfix);
        TryPatch(harmony, typeof(Mango), "AfterObtained", prefix, postfix);
        TryPatch(harmony, typeof(FakeMango), "AfterObtained", prefix, postfix);
        TryPatch(harmony, typeof(NutritiousOyster), "AfterObtained", prefix, postfix);
        TryPatch(harmony, typeof(LeesWaffle), "AfterObtained", prefix, postfix);

        // ── Food relics: heal on pickup / per-room / per-turn (Catalog §14 Gap #4) ──
        TryPatch(harmony, typeof(FakeLeesWaffle), "AfterObtained", prefix, postfix);
        TryPatch(harmony, typeof(BloodVial), "AfterPlayerTurnStartLate", prefix, postfix);
        TryPatch(harmony, typeof(FakeBloodVial), "AfterPlayerTurnStartLate", prefix, postfix);
        TryPatch(harmony, typeof(MealTicket), "AfterRoomEntered", prefix, postfix);
        TryPatch(harmony, typeof(EternalFeather), "AfterRoomEntered", prefix, postfix);
        TryPatch(harmony, typeof(ChosenCheese), "AfterCombatEnd", prefix, postfix);
        TryPatch(harmony, typeof(DragonFruit), "AfterGoldGained", prefix, postfix);

        // ── Combat-start power application relics (Catalog §3 Gap #5) ──
        // Apply Strength/Focus inside AfterRoomEntered → power source must be recorded
        // as the relic so subsequent ModifyDamageAdditive bonuses attribute back here.
        TryPatch(harmony, typeof(Vajra), "AfterRoomEntered", prefix, postfix);
        TryPatch(harmony, typeof(Girya), "AfterRoomEntered", prefix, postfix);
        TryPatch(harmony, typeof(DataDisk), "AfterRoomEntered", prefix, postfix);

        // ── Round 9 batch: §18.1 missing relic-source attribution ──
        // Block: Anchor gives 10 block at combat start.
        TryPatch(harmony, typeof(Anchor), "BeforeCombatStart", prefix, postfix);
        // Indirect damage (Thorns) — same shape as Akabeko/Vajra: apply power inside hook,
        // power source becomes the relic, subsequent reflected damage attributes back.
        TryPatch(harmony, typeof(BronzeScales), "AfterRoomEntered", prefix, postfix);
        // Strength buff for full combat — same shape as Vajra.
        TryPatch(harmony, typeof(EmberTea), "AfterRoomEntered", prefix, postfix);
        // Energy: gain 4 energy at combat start — same shape as Lantern.
        TryPatch(harmony, typeof(VeryHotCocoa), "AfterSideTurnStart", prefix, postfix);
        // Draw: zero-cost draw at start (similar to BagOfMarbles flow).
        TryPatch(harmony, typeof(PowerCell), "BeforeSideTurnStart", prefix, postfix);
        // Draw: free redraw whenever hand is emptied.
        TryPatch(harmony, typeof(UnceasingTop), "AfterHandEmptied", prefix, postfix);

        // ── Round 10 batch: §19.3 unpatched relics (catalog audit) ──
        // Stars: DivineDestiny grants stars on first side turn; DivineRight on room enter.
        TryPatch(harmony, typeof(DivineDestiny), "AfterSideTurnStart", prefix, postfix);
        TryPatch(harmony, typeof(DivineRight), "AfterRoomEntered", prefix, postfix);
        // Energy: PaelsFlesh grants energy starting round 3 (same shape as PaelsTears).
        TryPatch(harmony, typeof(PaelsFlesh), "AfterSideTurnStart", prefix, postfix);
        // Heal: LizardTail heals on prevent-death via CreatureCmd.Heal.
        TryPatch(harmony, typeof(LizardTail), "AfterPreventingDeath", prefix, postfix);

        // ── Round 11 batch: §19.3 remaining unpatched relics ──

        // Draw: Pocketwatch draws extra cards if few played this turn.
        TryPatch(harmony, typeof(Pocketwatch), "AfterSideTurnStart", prefix, postfix);
        // Upgrade: Bellows upgrades hand cards on first player turn.
        TryPatch(harmony, typeof(Bellows), "AfterPlayerTurnStart", prefix, postfix);
        // Upgrade: BoneTea upgrades hand on first round (consumes charge).
        TryPatch(harmony, typeof(BoneTea), "AfterSideTurnStart", prefix, postfix);
        // Debuff: TeaOfDiscourtesy adds Dazed cards before combat.
        TryPatch(harmony, typeof(TeaOfDiscourtesy), "BeforeCombatStart", prefix, postfix);
        // Power: FencingManual applies Forge (dexterity) on first turn.
        TryPatch(harmony, typeof(FencingManual), "AfterSideTurnStart", prefix, postfix);
        // Energy: Bread adjusts energy on first turn.
        TryPatch(harmony, typeof(Bread), "AfterSideTurnStart", prefix, postfix);
        // Track: LavaLamp tracks unblocked damage for card upgrade rewards.
        TryPatch(harmony, typeof(LavaLamp), "AfterDamageReceived", prefix, postfix);
        TryPatch(harmony, typeof(LavaLamp), "AfterRoomEntered", prefix, postfix);
        // Extra turn: PaelsEye grants extra turn if no cards played.
        TryPatch(harmony, typeof(PaelsEye), "AfterSideTurnStart", prefix, postfix);
        TryPatch(harmony, typeof(PaelsEye), "BeforeTurnEndEarly", prefix, postfix);
        TryPatch(harmony, typeof(PaelsEye), "AfterTakingExtraTurn", prefix, postfix);
        TryPatch(harmony, typeof(PaelsEye), "BeforeCardPlayed", prefix, postfix);
        // Post-combat: PaelsTooth grants stored upgraded card after combat.
        TryPatch(harmony, typeof(PaelsTooth), "AfterCombatEnd", prefix, postfix);
        // Damage: PenNib doubles damage of every 10th attack played.
        TryPatch(harmony, typeof(PenNib), "BeforeCardPlayed", prefix, postfix);
        TryPatch(harmony, typeof(PenNib), "AfterCardPlayed", prefix, postfix);
        // Card limit + energy: VelvetChoker restricts plays, adds max energy.
        TryPatch(harmony, typeof(VelvetChoker), "BeforeSideTurnStart", prefix, postfix);
        TryPatch(harmony, typeof(VelvetChoker), "AfterCardPlayed", prefix, postfix);
        // Enemy buff: PhilosophersStone gives enemies Strength on enter.
        TryPatch(harmony, typeof(PhilosophersStone), "AfterCreatureAddedToCombat", prefix, postfix);
        TryPatch(harmony, typeof(PhilosophersStone), "AfterRoomEntered", prefix, postfix);
        // Pickup: PaelsClaw enchants 3 cards with Goopy.
        TryPatch(harmony, typeof(PaelsClaw), "AfterObtained", prefix, postfix);
        // Pickup: PaelsGrowth enchants 1 card with Clone.
        TryPatch(harmony, typeof(PaelsGrowth), "AfterObtained", prefix, postfix);
        // Pickup: PaelsHorn adds 2 Relax cards to deck.
        TryPatch(harmony, typeof(PaelsHorn), "AfterObtained", prefix, postfix);
        // Sacrifice: PaelsWing sacrifice alternative for card rewards.
        TryPatch(harmony, typeof(PaelsWing), "OnSacrifice", prefix, postfix);
        // Pickup: PunchDagger enchants card with Momentum.
        TryPatch(harmony, typeof(PunchDagger), "AfterObtained", prefix, postfix);

        // ── Round 14 v5+ audit: starter + common relics missed by earlier batches ──
        // Necrobinder starter: soul-bind on combat start, energy refund on reset.
        TryPatch(harmony, typeof(BoundPhylactery), "BeforeCombatStart", prefix, postfix);
        TryPatch(harmony, typeof(BoundPhylactery), "AfterEnergyResetLate", prefix, postfix);
        TryPatch(harmony, typeof(PhylacteryUnbound), "BeforeCombatStart", prefix, postfix);
        TryPatch(harmony, typeof(PhylacteryUnbound), "AfterSideTurnStart", prefix, postfix);
        // Defect starter: channels Lightning on combat start.
        TryPatch(harmony, typeof(CrackedCore), "BeforeSideTurnStart", prefix, postfix);
        TryPatch(harmony, typeof(InfusedCore), "AfterSideTurnStart", prefix, postfix);
        // Block: Gorget grants block on first room enter.
        TryPatch(harmony, typeof(Gorget), "AfterRoomEntered", prefix, postfix);
        // Rest heal boost.
        TryPatch(harmony, typeof(RegalPillow), "AfterRestSiteHeal", prefix, postfix);
        TryPatch(harmony, typeof(RegalPillow), "AfterRoomEntered", prefix, postfix);
        // Energy refund + room enter charge.
        TryPatch(harmony, typeof(VenerableTeaSet), "AfterRoomEntered", prefix, postfix);
        TryPatch(harmony, typeof(VenerableTeaSet), "AfterEnergyReset", prefix, postfix);
        TryPatch(harmony, typeof(FakeVenerableTeaSet), "AfterRoomEntered", prefix, postfix);
        TryPatch(harmony, typeof(FakeVenerableTeaSet), "AfterEnergyReset", prefix, postfix);

        // ── Round 15 relic coverage sweep (relics.md) ──
        // Large batch covering remaining tracked-in-combat relics. Each entry
        // sets _activeRelicId for the duration of the hook so any damage /
        // block / heal / draw side-effect the hook produces attributes back
        // to the relic. Hooks that don't produce combat effects are patched
        // defensively (harmless no-op).

        // Card-pile movement triggers.
        TryPatch(harmony, typeof(BookOfFiveRings), "AfterCardChangedPiles", prefix, postfix);
        TryPatch(harmony, typeof(DarkstonePeriapt), "AfterCardChangedPiles", prefix, postfix);

        // Combat-start / room-enter passives.
        TryPatch(harmony, typeof(OddlySmoothStone), "AfterRoomEntered", prefix, postfix);
        TryPatch(harmony, typeof(Planisphere), "AfterRoomEntered", prefix, postfix);
        TryPatch(harmony, typeof(StoneCracker), "AfterRoomEntered", prefix, postfix);
        TryPatch(harmony, typeof(Pantograph), "AfterRoomEntered", prefix, postfix);
        TryPatch(harmony, typeof(SwordOfJade), "AfterRoomEntered", prefix, postfix);
        TryPatch(harmony, typeof(PumpkinCandle), "AfterRoomEntered", prefix, postfix);
        TryPatch(harmony, typeof(PumpkinCandle), "AfterObtained", prefix, postfix);
        TryPatch(harmony, typeof(BigMushroom), "AfterRoomEntered", prefix, postfix);
        TryPatch(harmony, typeof(BigMushroom), "AfterObtained", prefix, postfix);
        TryPatch(harmony, typeof(LoomingFruit), "AfterObtained", prefix, postfix);

        // Power amount modifiers.
        TryPatch(harmony, typeof(SneckoSkull), "AfterModifyingPowerAmountGiven", prefix, postfix);
        TryPatch(harmony, typeof(RuinedHelmet), "AfterModifyingPowerAmountReceived", prefix, postfix);
        TryPatch(harmony, typeof(RuinedHelmet), "AfterCombatEnd", prefix, postfix);

        // Per-attack proc.
        TryPatch(harmony, typeof(BoneFlute), "AfterAttack", prefix, postfix);

        // Vambrace: block bonus on first card each turn.
        TryPatch(harmony, typeof(Vambrace), "BeforeCombatStart", prefix, postfix);
        TryPatch(harmony, typeof(Vambrace), "AfterCardPlayed", prefix, postfix);
        TryPatch(harmony, typeof(Vambrace), "AfterCombatEnd", prefix, postfix);
        TryPatch(harmony, typeof(Vambrace), "AfterModifyingBlockAmount", prefix, postfix);

        // Potion synergy.
        TryPatch(harmony, typeof(ReptileTrinket), "AfterPotionUsed", prefix, postfix);
        TryPatch(harmony, typeof(BeltBuckle), "AfterPotionUsed", prefix, postfix);
        TryPatch(harmony, typeof(BeltBuckle), "AfterPotionProcured", prefix, postfix);
        TryPatch(harmony, typeof(BeltBuckle), "AfterObtained", prefix, postfix);

        // Block-clear trigger.
        TryPatch(harmony, typeof(SparklingRouge), "AfterBlockCleared", prefix, postfix);

        // Damage-received triggers.
        TryPatch(harmony, typeof(SelfFormingClay), "AfterDamageReceived", prefix, postfix);
        TryPatch(harmony, typeof(BeatingRemnant), "AfterDamageReceived", prefix, postfix);
        TryPatch(harmony, typeof(BeatingRemnant), "AfterModifyingHpLostAfterOsty", prefix, postfix);
        TryPatch(harmony, typeof(BeatingRemnant), "BeforeSideTurnStart", prefix, postfix);

        // Turn start / side turn start triggers.
        TryPatch(harmony, typeof(TwistedFunnel), "BeforeSideTurnStart", prefix, postfix);
        TryPatch(harmony, typeof(FuneraryMask), "AfterSideTurnStart", prefix, postfix);
        TryPatch(harmony, typeof(SymbioticVirus), "AfterSideTurnStart", prefix, postfix);
        TryPatch(harmony, typeof(OrangeDough), "AfterSideTurnStart", prefix, postfix);
        TryPatch(harmony, typeof(BigHat), "AfterSideTurnStart", prefix, postfix);
        TryPatch(harmony, typeof(Sai), "AfterSideTurnStart", prefix, postfix);
        TryPatch(harmony, typeof(Crossbow), "AfterSideTurnStart", prefix, postfix);
        TryPatch(harmony, typeof(SealOfGold), "AfterSideTurnStart", prefix, postfix);
        TryPatch(harmony, typeof(PollinousCore), "BeforeSideTurnStart", prefix, postfix);
        TryPatch(harmony, typeof(PollinousCore), "AfterCombatEnd", prefix, postfix);
        TryPatch(harmony, typeof(GamblingChip), "AfterPlayerTurnStart", prefix, postfix);
        TryPatch(harmony, typeof(VexingPuzzlebox), "AfterPlayerTurnStart", prefix, postfix);
        TryPatch(harmony, typeof(ChoicesParadox), "AfterPlayerTurnStart", prefix, postfix);

        // After-card-played triggers.
        TryPatch(harmony, typeof(MummifiedHand), "AfterCardPlayed", prefix, postfix);
        TryPatch(harmony, typeof(RazorTooth), "AfterCardPlayed", prefix, postfix);
        TryPatch(harmony, typeof(HelicalDart), "AfterCardPlayed", prefix, postfix);

        // Before-card-played triggers.
        TryPatch(harmony, typeof(ChemicalX), "BeforeCardPlayed", prefix, postfix);

        // Osty-related modifiers.
        TryPatch(harmony, typeof(TungstenRod), "AfterModifyingHpLostAfterOsty", prefix, postfix);
        TryPatch(harmony, typeof(TheBoot), "AfterModifyingHpLostBeforeOsty", prefix, postfix);

        // Turn-end / shuffle triggers.
        TryPatch(harmony, typeof(Bookmark), "AfterTurnEnd", prefix, postfix);
        TryPatch(harmony, typeof(TheAbacus), "AfterShuffle", prefix, postfix);

        // Hand draw triggers.
        TryPatch(harmony, typeof(Toolbox), "BeforeHandDraw", prefix, postfix);
        TryPatch(harmony, typeof(NinjaScroll), "BeforeHandDraw", prefix, postfix);
        TryPatch(harmony, typeof(RadiantPearl), "BeforeHandDraw", prefix, postfix);
        TryPatch(harmony, typeof(ToastyMittens), "BeforeHandDraw", prefix, postfix);
        TryPatch(harmony, typeof(JeweledMask), "BeforeHandDraw", prefix, postfix);

        // Stars relic: MiniRegent.
        TryPatch(harmony, typeof(MiniRegent), "AfterStarsSpent", prefix, postfix);
        TryPatch(harmony, typeof(MiniRegent), "BeforeSideTurnStart", prefix, postfix);
        TryPatch(harmony, typeof(MiniRegent), "AfterCombatEnd", prefix, postfix);

        // Orb / magnitude modifiers.
        TryPatch(harmony, typeof(GoldPlatedCables), "AfterModifyingOrbPassiveTriggerCount", prefix, postfix);

        // BurningSticks: exhaust synergy.
        TryPatch(harmony, typeof(BurningSticks), "AfterCardExhausted", prefix, postfix);
        TryPatch(harmony, typeof(BurningSticks), "AfterRoomEntered", prefix, postfix);
        TryPatch(harmony, typeof(BurningSticks), "AfterCombatEnd", prefix, postfix);

        // Rest-site heal modifiers.
        TryPatch(harmony, typeof(StoneHumidifier), "AfterRestSiteHeal", prefix, postfix);

        // PaelsLegion: minion-class Regent relic.
        TryPatch(harmony, typeof(MegaCrit.Sts2.Core.Models.Relics.PaelsLegion), "BeforeCombatStart", prefix, postfix);
        TryPatch(harmony, typeof(MegaCrit.Sts2.Core.Models.Relics.PaelsLegion), "AfterSideTurnStart", prefix, postfix);
        TryPatch(harmony, typeof(MegaCrit.Sts2.Core.Models.Relics.PaelsLegion), "AfterCardPlayed", prefix, postfix);
        TryPatch(harmony, typeof(MegaCrit.Sts2.Core.Models.Relics.PaelsLegion), "AfterCombatEnd", prefix, postfix);

        // BrilliantScarf / DiamondDiadem / MusicBox / UnsettlingLamp — card-play listeners.
        TryPatch(harmony, typeof(BrilliantScarf), "AfterCardPlayed", prefix, postfix);
        TryPatch(harmony, typeof(BrilliantScarf), "BeforeSideTurnStart", prefix, postfix);
        TryPatch(harmony, typeof(BrilliantScarf), "AfterCombatEnd", prefix, postfix);
        TryPatch(harmony, typeof(DiamondDiadem), "AfterCardPlayed", prefix, postfix);
        TryPatch(harmony, typeof(DiamondDiadem), "AfterSideTurnStart", prefix, postfix);
        TryPatch(harmony, typeof(DiamondDiadem), "BeforeTurnEnd", prefix, postfix);
        TryPatch(harmony, typeof(DiamondDiadem), "AfterCombatEnd", prefix, postfix);
        TryPatch(harmony, typeof(MusicBox), "BeforeCardPlayed", prefix, postfix);
        TryPatch(harmony, typeof(MusicBox), "AfterCardPlayed", prefix, postfix);
        TryPatch(harmony, typeof(MusicBox), "BeforeSideTurnStart", prefix, postfix);
        TryPatch(harmony, typeof(MusicBox), "AfterCombatEnd", prefix, postfix);
        TryPatch(harmony, typeof(UnsettlingLamp), "BeforeCombatStart", prefix, postfix);
        TryPatch(harmony, typeof(UnsettlingLamp), "AfterCardPlayed", prefix, postfix);
        TryPatch(harmony, typeof(UnsettlingLamp), "AfterCombatEnd", prefix, postfix);

        // Regalite: combat enter / card spawn.
        TryPatch(harmony, typeof(Regalite), "AfterCardEnteredCombat", prefix, postfix);

        // BookRepairKnife: Doom resurrection heal.
        TryPatch(harmony, typeof(BookRepairKnife), "AfterDiedToDoom", prefix, postfix);

        // SneckoEye / FakeSneckoEye / FakeAnchor: combat start passives.
        TryPatch(harmony, typeof(SneckoEye), "BeforeCombatStart", prefix, postfix);
        TryPatch(harmony, typeof(SneckoEye), "AfterObtained", prefix, postfix);
        TryPatch(harmony, typeof(FakeSneckoEye), "BeforeCombatStart", prefix, postfix);
        TryPatch(harmony, typeof(FakeSneckoEye), "AfterObtained", prefix, postfix);
        TryPatch(harmony, typeof(FakeAnchor), "BeforeCombatStart", prefix, postfix);
    }

    private static void TryPatch(Harmony harmony, Type type, string methodName,
        HarmonyMethod prefix, HarmonyMethod postfix)
    {
        try
        {
            var method = AccessTools.Method(type, methodName);
            if (method == null)
            {
                Safe.Warn($"[RelicPatch] Method not found: {type.Name}.{methodName}");
                return;
            }
            harmony.Patch(method, prefix, postfix);
        }
        catch (Exception ex)
        {
            Safe.Warn($"[RelicPatch] Failed {type.Name}.{methodName}: {ex.Message}");
        }
    }
}

/// <summary>
/// Manual PowerHookContext patcher. Patches each power's hook method individually
/// with try/catch so one failure doesn't prevent other powers from being patched.
/// Prefix: sets the active power source on CombatTracker.
/// Postfix: clears the active power source.
/// </summary>
public static class PowerHookContextPatcher
{
    // P2-5: Powers whose hook draws multiple cards in a single invocation.
    // The pending draw source is made sticky for N draws so powers like Pagestorm
    // (draws `Amount` cards when ethereal card drawn) fully attribute all extra
    // draws, not just the first. Count is resolved from PowerModel.Amount at
    // hook-fire time so upgraded/scaled powers attribute the correct number.
    private static readonly HashSet<string> _powerMultiDrawIds = new()
    {
        "PAGESTORM_POWER",
    };

    public static void SetPowerContext(PowerModel __instance)
    {
        Safe.Run(() =>
        {
            var powerId = __instance.Id.Entry;
            CombatTracker.Instance.SetActivePowerSource(powerId);
            // Also set pending draw/block/damage source for async power hooks.
            // These persist through async await because they're AsyncLocal-backed.
            // Consumed by OnCardDrawn/OnBlockGained after use.
            var source = ContributionMap.Instance.GetPowerSource(powerId);
            if (source != null)
            {
                if (_powerMultiDrawIds.Contains(powerId))
                {
                    int count = Math.Max(1, (int)__instance.Amount);
                    CombatTracker.Instance.SetPendingDrawSource(
                        source.SourceId, source.SourceType, count);
                }
                else
                {
                    CombatTracker.Instance.SetPendingDrawSource(
                        source.SourceId, source.SourceType);
                }
            }
        });
    }

    public static void ClearPowerContext()
    {
        Safe.Run(() =>
        {
            CombatTracker.Instance.ClearActivePowerSource();
            // DON'T clear pending draw source — it persists through async and is
            // consumed by OnCardDrawn. If no draw happens, ForceResetAllContext
            // cleans it up between tests.
        });
    }

    public static void PatchAll(Harmony harmony)
    {
        var prefix = new HarmonyMethod(AccessTools.Method(typeof(PowerHookContextPatcher), nameof(SetPowerContext)));
        var postfix = new HarmonyMethod(AccessTools.Method(typeof(PowerHookContextPatcher), nameof(ClearPowerContext)));

        // ── Ironclad ──
        TryPatch(harmony, typeof(RagePower), "AfterCardPlayed", prefix, postfix);
        TryPatch(harmony, typeof(FlameBarrierPower), "AfterDamageReceived", prefix, postfix);
        TryPatch(harmony, typeof(FeelNoPainPower), "AfterCardExhausted", prefix, postfix);
        TryPatch(harmony, typeof(PlatingPower), "BeforeTurnEndEarly", prefix, postfix);
        TryPatch(harmony, typeof(InfernoPower), "AfterDamageReceived", prefix, postfix);
        TryPatch(harmony, typeof(InfernoPower), "AfterPlayerTurnStart", prefix, postfix);
        TryPatch(harmony, typeof(JuggernautPower), "AfterBlockGained", prefix, postfix);
        TryPatch(harmony, typeof(CrimsonMantlePower), "AfterPlayerTurnStart", prefix, postfix);
        TryPatch(harmony, typeof(DarkEmbracePower), "AfterCardExhausted", prefix, postfix);
        TryPatch(harmony, typeof(DarkEmbracePower), "AfterTurnEnd", prefix, postfix);
        // Fix 3.3 (Round 14 v5): DemonForm applies Str+N at turn start.
        // Without this patch, the Strength apply sees no power context, so
        // the global STRENGTH_POWER source list stays empty, and Strike's
        // ModifierDamage decomposition falls through DistributeByPowerSources
        // to the default "(STRENGTH_POWER, power, total)" fallback, showing
        // "Strength_power" in the attack panel instead of DEMON_FORM.
        TryPatch(harmony, typeof(DemonFormPower), "AfterSideTurnStart", prefix, postfix);
        // Rupture: self-damage during player turn → gain 1 Str per hit.
        // Same symptom as DemonForm — needs power context for the Str apply.
        TryPatch(harmony, typeof(RupturePower), "AfterDamageReceived", prefix, postfix);
        TryPatch(harmony, typeof(RupturePower), "AfterCardPlayed", prefix, postfix);

        // ── Silent ──
        TryPatch(harmony, typeof(PoisonPower), "AfterSideTurnStart", prefix, postfix);
        TryPatch(harmony, typeof(OutbreakPower), "AfterPowerAmountChanged", prefix, postfix);
        TryPatch(harmony, typeof(PanachePower), "AfterCardPlayed", prefix, postfix);
        TryPatch(harmony, typeof(AfterimagePower), "AfterCardPlayed", prefix, postfix);
        TryPatch(harmony, typeof(SneakyPower), "AfterCardPlayed", prefix, postfix);
        TryPatch(harmony, typeof(ViciousPower), "AfterPowerAmountChanged", prefix, postfix);
        TryPatch(harmony, typeof(EnvenomPower), "AfterDamageGiven", prefix, postfix);
        TryPatch(harmony, typeof(NoxiousFumesPower), "AfterSideTurnStart", prefix, postfix);
        TryPatch(harmony, typeof(InfiniteBladesPower), "BeforeHandDraw", prefix, postfix);

        // ── Defect ──
        TryPatch(harmony, typeof(StormPower), "AfterCardPlayed", prefix, postfix);
        TryPatch(harmony, typeof(HailstormPower), "BeforeTurnEnd", prefix, postfix);
        TryPatch(harmony, typeof(AutomationPower), "AfterCardDrawn", prefix, postfix);
        TryPatch(harmony, typeof(IterationPower), "AfterCardDrawn", prefix, postfix);
        TryPatch(harmony, typeof(SubroutinePower), "BeforeCardPlayed", prefix, postfix);
        TryPatch(harmony, typeof(SubroutinePower), "AfterCardPlayed", prefix, postfix);
        TryPatch(harmony, typeof(ConsumingShadowPower), "AfterTurnEnd", prefix, postfix);
        TryPatch(harmony, typeof(LoopPower), "AfterPlayerTurnStart", prefix, postfix);
        TryPatch(harmony, typeof(LightningRodPower), "AfterEnergyReset", prefix, postfix);
        TryPatch(harmony, typeof(SpinnerPower), "AfterEnergyReset", prefix, postfix);
        TryPatch(harmony, typeof(CreativeAiPower), "BeforeHandDraw", prefix, postfix);
        TryPatch(harmony, typeof(HelloWorldPower), "BeforeHandDraw", prefix, postfix);
        TryPatch(harmony, typeof(ThunderPower), "AfterOrbEvoked", prefix, postfix);

        // ── Necrobinder ──
        TryPatch(harmony, typeof(DanseMacabrePower), "BeforeCardPlayed", prefix, postfix);
        TryPatch(harmony, typeof(CountdownPower), "AfterSideTurnStart", prefix, postfix);
        TryPatch(harmony, typeof(HauntPower), "AfterCardPlayed", prefix, postfix);
        TryPatch(harmony, typeof(PagestormPower), "AfterCardDrawn", prefix, postfix);
        TryPatch(harmony, typeof(ShroudPower), "AfterPowerAmountChanged", prefix, postfix);
        TryPatch(harmony, typeof(SleightOfFleshPower), "AfterPowerAmountChanged", prefix, postfix);
        TryPatch(harmony, typeof(OblivionPower), "AfterCardPlayed", prefix, postfix);
        TryPatch(harmony, typeof(CallOfTheVoidPower), "BeforeHandDraw", prefix, postfix);
        TryPatch(harmony, typeof(NecroMasteryPower), "AfterCurrentHpChanged", prefix, postfix);
        TryPatch(harmony, typeof(DevourLifePower), "AfterCardPlayed", prefix, postfix);
        TryPatch(harmony, typeof(SpiritOfAshPower), "BeforeCardPlayed", prefix, postfix);
        TryPatch(harmony, typeof(ReaperFormPower), "AfterDamageGiven", prefix, postfix);
        TryPatch(harmony, typeof(SicEmPower), "AfterDamageGiven", prefix, postfix);

        // ── Regent ──
        TryPatch(harmony, typeof(BlackHolePower), "AfterCardPlayed", prefix, postfix);
        TryPatch(harmony, typeof(BlackHolePower), "AfterStarsGained", prefix, postfix);
        TryPatch(harmony, typeof(ChildOfTheStarsPower), "AfterStarsSpent", prefix, postfix);
        TryPatch(harmony, typeof(ParryPower), "AfterSovereignBladePlayed", prefix, postfix);
        TryPatch(harmony, typeof(PillarOfCreationPower), "AfterCardGeneratedForCombat", prefix, postfix);
        TryPatch(harmony, typeof(OrbitPower), "AfterEnergySpent", prefix, postfix);
        // ArsenalPower in the current game: applies StrengthPower to owner on
        // AfterCardGeneratedForCombat (not AfterCardPlayed). Without setting
        // power source context at THAT hook, the Str apply attributes to the
        // card that generated the new card (BladeOfInk, Stoke, etc.) instead
        // of Arsenal. Adding the context patch here fixes attribution.
        TryPatch(harmony, typeof(ArsenalPower), "AfterCardGeneratedForCombat", prefix, postfix);
        TryPatch(harmony, typeof(MonologuePower), "AfterCardPlayed", prefix, postfix);
        TryPatch(harmony, typeof(MonarchsGazePower), "AfterDamageGiven", prefix, postfix);
        TryPatch(harmony, typeof(SpectrumShiftPower), "BeforeHandDraw", prefix, postfix);
        TryPatch(harmony, typeof(ReflectPower), "AfterDamageReceived", prefix, postfix);
        TryPatch(harmony, typeof(GenesisPower), "AfterEnergyReset", prefix, postfix);
        TryPatch(harmony, typeof(TheSealedThronePower), "BeforeCardPlayed", prefix, postfix);
        TryPatch(harmony, typeof(FurnacePower), "AfterSideTurnStart", prefix, postfix);
        TryPatch(harmony, typeof(SentryModePower), "BeforeHandDraw", prefix, postfix);

        // ── Next-turn powers ──
        TryPatch(harmony, typeof(BlockNextTurnPower), "AfterBlockCleared", prefix, postfix);
        TryPatch(harmony, typeof(StarNextTurnPower), "AfterEnergyReset", prefix, postfix);

        // ── Round 14 v5 review §2: missing contribution-producing Power hooks ──
        // Ironclad / general card-played triggers that apply Str/Block indirectly.
        TryPatch(harmony, typeof(EnragePower), "AfterCardPlayed", prefix, postfix);
        TryPatch(harmony, typeof(GalvanicPower), "AfterCardPlayed", prefix, postfix);
        // Draw / hand-manipulation powers.
        TryPatch(harmony, typeof(JugglingPower), "AfterCardPlayed", prefix, postfix);
        TryPatch(harmony, typeof(PrepTimePower), "AfterSideTurnStart", prefix, postfix);
        TryPatch(harmony, typeof(MayhemPower), "BeforeHandDrawLate", prefix, postfix);
        TryPatch(harmony, typeof(StratagemPower), "AfterShuffle", prefix, postfix);
        // Energy-on-turn-start powers (AfterEnergyReset fires once per player turn).
        TryPatch(harmony, typeof(RadiancePower), "AfterEnergyReset", prefix, postfix);
        TryPatch(harmony, typeof(EnergyNextTurnPower), "AfterEnergyReset", prefix, postfix);
        // Burst / EchoForm duplicate next card plays — treat their effect-setup hook
        // as the attribution context for the duplicated plays.
        TryPatch(harmony, typeof(BurstPower), "AfterModifyingCardPlayCount", prefix, postfix);
        TryPatch(harmony, typeof(BurstPower), "AfterTurnEnd", prefix, postfix);
        TryPatch(harmony, typeof(EchoFormPower), "AfterModifyingCardPlayCount", prefix, postfix);
        TryPatch(harmony, typeof(MasterPlannerPower), "AfterCardPlayed", prefix, postfix);

        // ── Thorns ──
        TryPatch(harmony, typeof(ThornsPower), "BeforeDamageReceived", prefix, postfix);

        // ── Healing powers ──
        TryPatch(harmony, typeof(RegenPower), "AfterTurnEnd", prefix, postfix);

        // ── Shared / Colorless ──
        TryPatch(harmony, typeof(SmokestackPower), "AfterCardGeneratedForCombat", prefix, postfix);
        TryPatch(harmony, typeof(RollingBoulderPower), "AfterPlayerTurnStart", prefix, postfix);
        // Fix 3.5 (Round 14 v5): RollingBoulder's non-test path dispatches damage
        // through a Godot NRollingBoulderVfx signal callback (Callable.From(...)).
        // The signal handler runs in a fresh ExecutionContext, so AsyncLocal values
        // set by the AfterPlayerTurnStart prefix don't propagate to DoDamage.
        // Patching the private DoDamage method ensures the power context is
        // re-established at the actual damage dispatch site, regardless of which
        // async path reached it.
        TryPatch(harmony, typeof(RollingBoulderPower), "DoDamage", prefix, postfix);
        TryPatch(harmony, typeof(SpeedsterPower), "AfterCardDrawn", prefix, postfix);
        TryPatch(harmony, typeof(SerpentFormPower), "AfterCardPlayed", prefix, postfix);
        TryPatch(harmony, typeof(TrashToTreasurePower), "AfterCardGeneratedForCombat", prefix, postfix);
        // StampedePower.BeforeTurnEnd not patchable at runtime (not implemented, only inherited)
        // Stampede's auto-played cards generate their own CardPlay events.

        // NOTE: TemporaryStrengthPower.AfterTurnEnd is handled by TempStrengthRevertPatch
        // with specific revert logic, not the generic Set/Clear power context.
    }

    private static void TryPatch(Harmony harmony, Type type, string methodName,
        HarmonyMethod prefix, HarmonyMethod postfix)
    {
        try
        {
            var method = AccessTools.Method(type, methodName);
            if (method == null)
            {
                Safe.Warn($"[PowerPatch] Method not found: {type.Name}.{methodName}");
                return;
            }
            harmony.Patch(method, prefix, postfix);
        }
        catch (Exception ex)
        {
            Safe.Warn($"[PowerPatch] Failed {type.Name}.{methodName}: {ex.Message}");
        }
    }
}

// ═══════════════════════════════════════════════════════════
// Module A+F: Damage/Block modifier attribution
// Patches Hook.ModifyDamageInternal and Hook.ModifyBlock to capture
// per-modifier contributions (Strength, Vulnerable, Dexterity, Frail, etc.)
// ═══════════════════════════════════════════════════════════

[HarmonyPatch]
public static class DamageModifierPatch
{
    /// <summary>
    /// Patch Hook.ModifyDamage (the public entry point) to capture base damage before
    /// modifiers and the final result after, then record per-modifier contributions.
    /// </summary>
    [HarmonyPatch(typeof(Hook), nameof(Hook.ModifyDamage))]
    [HarmonyPrefix]
    public static void BeforeModifyDamage(decimal damage, out decimal __state)
    {
        __state = damage;
        // Fix-3: Clear stale modifiers from previous call to prevent leaking
        ContributionMap.Instance.LastDamageModifiers.Clear();
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.ModifyDamage))]
    [HarmonyPostfix]
    public static void AfterModifyDamage(decimal __result, decimal __state,
        Creature? target, Creature? dealer, ValueProp props, CardModel? cardSource,
        IEnumerable<AbstractModel> modifiers)
    {
        Safe.Run(() =>
        {
            // Only track damage dealt by player to enemies
            if (dealer == null || !dealer.IsPlayer || target == null || target.IsPlayer)
                return;
            // IsPoweredAttack = has Move flag and not Unpowered
            if (!props.HasFlag(ValueProp.Move) || props.HasFlag(ValueProp.Unpowered))
                return;

            decimal baseDmg = __state;
            decimal finalDmg = __result;
            decimal totalBonus = finalDmg - baseDmg;

            if (totalBonus <= 0 || modifiers == null)
                return;

            var modList = ContributionMap.Instance.LastDamageModifiers;
            modList.Clear();

            // Attribute modifier bonuses to their power/relic sources
            foreach (var mod in modifiers)
            {
                if (mod is PowerModel power)
                {
                    var powerId = power.Id.Entry;
                    if (string.IsNullOrEmpty(powerId)) continue;

                    // For additive modifiers (Strength, Vigor, etc.), get the additive contribution
                    decimal additive = power.ModifyDamageAdditive(target, baseDmg, props, dealer, cardSource);

                    if (additive != 0)
                    {
                        // Multi-source: distribute additive bonus proportionally
                        int totalAdd = Math.Abs((int)additive);
                        var distributed = ContributionMap.Instance.DistributeByPowerSources(powerId, totalAdd);
                        foreach (var (srcId, srcType, share) in distributed)
                            modList.Add(new ContributionMap.ModifierContribution(srcId, srcType, share));
                        continue;
                    }

                    // For multiplicative modifiers
                    decimal multiplicative = power.ModifyDamageMultiplicative(target, baseDmg + additive, props, dealer, cardSource);
                    if (multiplicative == 1m || multiplicative <= 0) continue;

                    // Round 9 round 53: skip multiplicative modifiers < 1.
                    // This function runs on the player→enemy path only, so any
                    // `multiplicative < 1` is a debuff on the PLAYER (e.g. Weak)
                    // that REDUCES outgoing damage. Attributing the reduction as
                    // a positive damage contribution (as DecomposeWeakContribution
                    // used to) was turning enemy-applied Weak into a bogus
                    // "+2 damage" row in the contribution panel. Defensive
                    // mitigation tracking (MitigatedByDebuff) happens separately
                    // via OnWeakMitigation on the enemy→player path.
                    if (multiplicative < 1m) continue;

                    // H2-R: Decompose VulnerablePower internal modifiers into
                    // base / PaperPhrog / Cruelty / Debilitate sub-contributions.
                    if (powerId == "VULNERABLE_POWER")
                    {
                        DecomposeVulnerableContribution(power, target, dealer, props, cardSource,
                            finalDmg, multiplicative, modList);
                        continue;
                    }

                    // Standard multiplicative: contribution = finalDmg - finalDmg/multiplier
                    int contribution = (int)(finalDmg - finalDmg / multiplicative);
                    if (contribution == 0) continue;

                    // Multi-source: distribute multiplicative bonus proportionally
                    var multDistributed = ContributionMap.Instance.DistributeByPowerSources(
                        powerId, Math.Abs(contribution));
                    foreach (var (psId, psType, share) in multDistributed)
                        modList.Add(new ContributionMap.ModifierContribution(psId, psType, share));
                }
                else if (mod is RelicModel relic)
                {
                    var relicId = relic.Id.Entry;
                    if (string.IsNullOrEmpty(relicId)) continue;

                    decimal additive = relic.ModifyDamageAdditive(target, baseDmg, props, dealer, cardSource);
                    if (additive != 0)
                    {
                        modList.Add(new ContributionMap.ModifierContribution(
                            relicId, "relic", Math.Abs((int)additive)));
                        continue;
                    }

                    decimal multiplicative = relic.ModifyDamageMultiplicative(
                        target, baseDmg + additive, props, dealer, cardSource);
                    if (multiplicative != 1m && multiplicative > 0)
                    {
                        if (multiplicative < 1m) continue;
                        int contribution = (int)(finalDmg - finalDmg / multiplicative);
                        if (contribution > 0)
                            modList.Add(new ContributionMap.ModifierContribution(
                                relicId, "relic", contribution));
                    }
                }
            }
        });
    }

    /// <summary>
    /// H2-R: Decompose VulnerablePower's combined multiplier into individual contributions:
    /// Vulnerable base (1.5) + PaperPhrog (+0.25) + Cruelty (+Amount/100) + Debilitate (doubles bonus).
    /// Each sub-contributor gets a proportional share of the total Vulnerable damage bonus.
    /// </summary>
    private static void DecomposeVulnerableContribution(
        PowerModel vulnPower, Creature? target, Creature? dealer, ValueProp props, CardModel? cardSource,
        decimal finalDmg, decimal combinedMult,
        List<ContributionMap.ModifierContribution> modList)
    {
        // Total damage bonus from Vulnerable = finalDmg - finalDmg / combinedMult
        int totalContrib = (int)(finalDmg - finalDmg / combinedMult);
        if (totalContrib <= 0) return;

        // Base Vulnerable multiplier bonus (typically 0.5 from 1.5)
        decimal vulnBaseDelta = 0.5m;
        decimal phrogDelta = 0;
        decimal crueltyDelta = 0;
        decimal debilitateDelta = 0;

        // Check PaperPhrog relic on dealer
        if (dealer?.Player != null)
        {
            var phrog = dealer.Player.GetRelic<PaperPhrog>();
            if (phrog != null)
                phrogDelta = 0.25m;
        }

        // Check CrueltyPower on dealer
        if (dealer != null)
        {
            var cruelty = dealer.GetPower<CrueltyPower>();
            if (cruelty != null)
                crueltyDelta = (decimal)cruelty.Amount / 100m;
        }

        // Debilitate on target doubles the bonus
        if (target != null)
        {
            var debilitate = target.GetPower<DebilitatePower>();
            if (debilitate != null)
            {
                decimal preDebilMult = 1m + vulnBaseDelta + phrogDelta + crueltyDelta;
                debilitateDelta = preDebilMult - 1m; // doubles the bonus
            }
        }

        // Proportional share of total contribution
        decimal subTotal = vulnBaseDelta + phrogDelta + crueltyDelta + debilitateDelta;
        if (subTotal <= 0) return;

        // Vulnerable base — split among FIFO sources (Fix-4)
        int vulnShare = (int)Math.Round(totalContrib * (vulnBaseDelta / subTotal));
        if (vulnShare > 0 && target != null)
        {
            var fractions = ContributionMap.Instance.GetDebuffSourceFractions(target.GetHashCode(), "VULNERABLE_POWER");
            if (fractions.Count > 0)
            {
                foreach (var (srcId, srcType, frac) in fractions)
                {
                    int srcShare = (int)Math.Round(vulnShare * frac);
                    if (srcShare > 0)
                        modList.Add(new ContributionMap.ModifierContribution(srcId, srcType, srcShare));
                }
            }
            else
            {
                var vulnSource = ContributionMap.Instance.GetPowerSource("VULNERABLE_POWER");
                string vsId = vulnSource?.SourceId ?? "VULNERABLE_POWER";
                string vsType = vulnSource?.SourceType ?? "power";
                modList.Add(new ContributionMap.ModifierContribution(vsId, vsType, vulnShare));
            }
        }

        // PaperPhrog relic
        if (phrogDelta > 0)
        {
            int phrogShare = (int)Math.Round(totalContrib * (phrogDelta / subTotal));
            if (phrogShare > 0)
                modList.Add(new ContributionMap.ModifierContribution("PAPER_PHROG", "relic", phrogShare));
        }

        // CrueltyPower
        if (crueltyDelta > 0)
        {
            var crueltySource = ContributionMap.Instance.GetPowerSource("CRUELTY_POWER");
            int crueltyShare = (int)Math.Round(totalContrib * (crueltyDelta / subTotal));
            if (crueltyShare > 0)
                modList.Add(new ContributionMap.ModifierContribution(
                    crueltySource?.SourceId ?? "CRUELTY_POWER", crueltySource?.SourceType ?? "power", crueltyShare));
        }

        // DebilitatePower
        if (debilitateDelta > 0)
        {
            var debilSource = ContributionMap.Instance.GetPowerSource("DEBILITATE_POWER");
            int debilShare = (int)Math.Round(totalContrib * (debilitateDelta / subTotal));
            if (debilShare > 0)
                modList.Add(new ContributionMap.ModifierContribution(
                    debilSource?.SourceId ?? "DEBILITATE_POWER", debilSource?.SourceType ?? "power", debilShare));
        }
    }

    // Round 14 v5 review §11: DecomposeWeakContribution removed — dead code.
    // Replaced by BeforeModifyDamage's `if (multiplicative < 1m) continue;` gate
    // which skips Weak in the player→enemy damage path entirely. Weak's contribution
    // is now tracked exclusively as player-side MitigatedByDebuff via OnWeakMitigation.
}

// ═══════════════════════════════════════════════════════════
// C1: Enemy base damage capture (for str reduction formula)
// Captures enemy's base damage BEFORE modifiers so we can compute
// how much strength reduction actually mitigated.
// Also captures Colossus/Intangible reduction for enemy→player damage.
// ═══════════════════════════════════════════════════════════

[HarmonyPatch]
public static class EnemyDamageIntentPatch
{
    [HarmonyPatch(typeof(Hook), nameof(Hook.ModifyDamage))]
    [HarmonyPrefix]
    public static void BeforeModifyDamage_Enemy(decimal damage, Creature? target, Creature? dealer,
        out decimal __state)
    {
        __state = damage;
        Safe.Run(() =>
        {
            if (dealer == null || dealer.IsPlayer) return;
            ContributionMap.Instance.PendingEnemyBaseDamage = (int)damage;
            // Dealer's current StrengthPower at hit time — already reflects
            // any reductions we've recorded (e.g. Piercing Wail's -8 has
            // already been applied before the attack fires). Used in
            // CombatTracker.OnDamageDealt to reconstruct "damage that would
            // have been dealt without our reductions" = base + (currentStr +
            // totalReduction). This matters when the enemy had positive Str
            // (e.g. buffed) before our reduction: raw base alone undercounts
            // the actual damage we prevented per hit.
            var str = dealer.GetPower<StrengthPower>();
            ContributionMap.Instance.PendingEnemyCurrentStr = (int)(str?.Amount ?? 0);
        });
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.ModifyDamage))]
    [HarmonyPostfix]
    public static void AfterModifyDamage_Enemy(decimal __result, decimal __state,
        Creature? target, Creature? dealer, ValueProp props, CardModel? cardSource,
        IEnumerable<AbstractModel> modifiers)
    {
        Safe.Run(() =>
        {
            // Only track enemy→player damage reduction (Colossus, Intangible)
            if (target == null || !target.IsPlayer) return;
            if (dealer == null || dealer.IsPlayer) return;

            decimal baseDmg = __state;
            decimal finalDmg = __result;
            decimal reduced = baseDmg - finalDmg;
            if (reduced <= 0 || modifiers == null) return;

            foreach (var mod in modifiers)
            {
                if (mod is PowerModel power)
                {
                    var powerId = power.Id.Entry;
                    if (string.IsNullOrEmpty(powerId)) continue;

                    // Intangible: ModifyDamageCap reduced damage
                    if (powerId == "INTANGIBLE_POWER")
                    {
                        int prevented = (int)(baseDmg - finalDmg);
                        if (prevented > 0)
                            CombatTracker.Instance.OnIntangibleReduction((int)baseDmg, (int)finalDmg);
                    }
                    // Colossus: ModifyDamageMultiplicative with 0.5x
                    else if (powerId == "COLOSSUS_POWER")
                    {
                        int prevented = (int)(baseDmg - finalDmg);
                        if (prevented > 0)
                            CombatTracker.Instance.OnColossusMitigation(prevented, target.GetHashCode());
                    }
                }
                else if (mod is RelicModel relic)
                {
                    // Round 15: player relic that reduced incoming damage via
                    // a ModifyDamageMultiplicative override (UndyingSigil 0.5x
                    // when at low HP, etc.). Per PRD §防御归因, credit the
                    // prevented damage as MitigatedByBuff on the relic.
                    var relicId = relic.Id.Entry;
                    if (string.IsNullOrEmpty(relicId)) continue;

                    decimal multiplicative = relic.ModifyDamageMultiplicative(
                        target, baseDmg, props, dealer, cardSource);
                    // Only reductions (< 1m) count as defense here.
                    if (multiplicative >= 1m || multiplicative <= 0) continue;

                    // Per PRD: contribution = finalDmg * (1 - multiplicative) / multiplicative
                    // equivalent to finalDmg_pre_this_mod - finalDmg_post = baseDmg*(1-mult).
                    // Approximate with the post-all-mods finalDmg to stay conservative.
                    int prevented = (int)(finalDmg * (1m - multiplicative) / multiplicative);
                    if (prevented > 0)
                        CombatTracker.Instance.OnRelicIncomingDamageMitigation(relicId, prevented);
                }
            }
        });
    }
}

// ═══════════════════════════════════════════════════════════
// Fix-1: Decrement FIFO debuff layers when duration ticks down
// PowerModel.SetAmount is synchronous void — safe for Harmony.
// ═══════════════════════════════════════════════════════════

[HarmonyPatch]
public static class DebuffDurationPatch
{
    [HarmonyPatch(typeof(PowerModel), nameof(PowerModel.SetAmount))]
    [HarmonyPrefix]
    public static void BeforeSetAmount(PowerModel __instance, out int __state)
    {
        __state = __instance.Amount;
    }

    [HarmonyPatch(typeof(PowerModel), nameof(PowerModel.SetAmount))]
    [HarmonyPostfix]
    public static void AfterSetAmount(PowerModel __instance, int amount, int __state)
    {
        Safe.Run(() =>
        {
            // Only care about duration decrements (amount < old) on non-player creatures
            if (amount >= __state) return;
            var owner = __instance.Owner;
            if (owner == null || owner.IsPlayer) return;

            var powerId = __instance.Id.Entry;
            if (string.IsNullOrEmpty(powerId)) return;

            // Only decrement for duration-based debuffs we track (Vulnerable, Weak)
            if (powerId != "VULNERABLE_POWER" && powerId != "WEAK_POWER") return;

            int decremented = __state - amount;
            for (int i = 0; i < decremented; i++)
                ContributionMap.Instance.DecrementDebuffLayers(owner.GetHashCode(), powerId);
        });
    }
}

[HarmonyPatch]
public static class BlockModifierPatch
{
    [HarmonyPatch(typeof(Hook), nameof(Hook.ModifyBlock))]
    [HarmonyPrefix]
    public static void BeforeModifyBlock(decimal block, out decimal __state)
    {
        __state = block;
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.ModifyBlock))]
    [HarmonyPostfix]
    public static void AfterModifyBlock(decimal __result, decimal __state,
        Creature target, ValueProp props, CardModel? cardSource, CardPlay? cardPlay,
        IEnumerable<AbstractModel> modifiers)
    {
        Safe.Run(() =>
        {
            if (target == null || !target.IsPlayer) return;

            decimal baseBlock = __state;
            decimal finalBlock = __result;
            decimal totalBonus = finalBlock - baseBlock;

            if (totalBonus <= 0 || modifiers == null)
                return;

            var modList = ContributionMap.Instance.LastBlockModifiers;
            modList.Clear();

            // Mirror AfterModifyDamage attribution: query each power's actual hook
            // contribution rather than reading power.Amount (which is a duration
            // counter for debuffs like Frail and produces phantom positive block).
            // Negative contributions (multiplicative < 1m, i.e. Frail/NoBlock etc.
            // reducing player block) are filtered out — they are NOT defense
            // contributions and must not appear in the defense bar.
            foreach (var mod in modifiers)
            {
                if (mod is PowerModel power)
                {
                    var powerId = power.Id.Entry;
                    if (string.IsNullOrEmpty(powerId)) continue;

                    decimal additive = power.ModifyBlockAdditive(target, baseBlock, props, cardSource, cardPlay);
                    if (additive > 0)
                    {
                        int totalAdd = (int)additive;
                        if (totalAdd <= 0) continue;
                        var distributed = ContributionMap.Instance.DistributeByPowerSources(powerId, totalAdd);
                        foreach (var (srcId, srcType, share) in distributed)
                            modList.Add(new ContributionMap.ModifierContribution(srcId, srcType, share));
                        continue;
                    }

                    decimal multiplicative = power.ModifyBlockMultiplicative(target, baseBlock + additive, props, cardSource, cardPlay);
                    if (multiplicative == 1m || multiplicative <= 0) continue;
                    // Skip multiplicative < 1m — these are player-side block debuffs
                    // (Frail 0.75x etc.) that REDUCE block. Attributing their negative
                    // delta as a positive defense contribution was the phantom-bar bug.
                    if (multiplicative < 1m) continue;

                    int contribution = (int)(finalBlock - finalBlock / multiplicative);
                    if (contribution <= 0) continue;

                    var multDistributed = ContributionMap.Instance.DistributeByPowerSources(powerId, contribution);
                    foreach (var (psId, psType, share) in multDistributed)
                        modList.Add(new ContributionMap.ModifierContribution(psId, psType, share));
                }
                else if (mod is RelicModel relic)
                {
                    // Round 15: relic that boosts block via a ModifyBlock*
                    // override (e.g. VitruvianMinion 2x on Minion cards).
                    // Mirrors the RelicModel branch in AfterModifyDamage.
                    var relicId = relic.Id.Entry;
                    if (string.IsNullOrEmpty(relicId)) continue;

                    decimal additive = relic.ModifyBlockAdditive(target, baseBlock, props, cardSource, cardPlay);
                    if (additive > 0)
                    {
                        modList.Add(new ContributionMap.ModifierContribution(
                            relicId, "relic", (int)additive));
                        continue;
                    }

                    decimal multiplicative = relic.ModifyBlockMultiplicative(
                        target, baseBlock + additive, props, cardSource, cardPlay);
                    if (multiplicative == 1m || multiplicative <= 0) continue;
                    if (multiplicative < 1m) continue;

                    int contribution = (int)(finalBlock - finalBlock / multiplicative);
                    if (contribution > 0)
                        modList.Add(new ContributionMap.ModifierContribution(
                            relicId, "relic", contribution));
                }
            }
        });
    }
}

// ═══════════════════════════════════════════════════════════
// Module C: Energy gain tracking
// ═══════════════════════════════════════════════════════════

[HarmonyPatch]
public static class EnergyGainPatch
{
    [HarmonyPatch(typeof(PlayerCmd), nameof(PlayerCmd.GainEnergy))]
    [HarmonyPostfix]
    public static void AfterGainEnergy(decimal amount)
    {
        Safe.Run(() =>
        {
            if (amount > 0)
                CombatTracker.Instance.OnEnergyGained((int)amount);
        });
    }
}

// ═══════════════════════════════════════════════════════════
// Module E: Card origin tracking (generated/transformed cards)
// ═══════════════════════════════════════════════════════════

[HarmonyPatch]
public static class CardOriginPatch
{
    /// <summary>
    /// Tracks CardCmd.Transform to record that the new card originated from
    /// the card that was being played (PrimalForce transforms attacks to GiantRock).
    /// </summary>
    [HarmonyPatch(typeof(CardCmd), nameof(CardCmd.Transform), new Type[] { typeof(CardModel), typeof(CardModel), typeof(CardPreviewStyle) })]
    [HarmonyPostfix]
    public static void AfterTransform(CardModel original, CardModel replacement)
    {
        Safe.Run(() =>
        {
            if (replacement == null) return;
            var replacementId = replacement.Id.Entry;
            if (replacementId == null) return;

            // Use the currently-playing card (e.g. BEGONE!, CHARGE!!) as origin,
            // falling back to the original card ID if no active context.
            var activeCard = CombatTracker.Instance.ActiveCardId;
            var originId = activeCard ?? original?.Id.Entry ?? "unknown";
            CombatTracker.Instance.RecordCardOrigin(
                replacement.GetHashCode(), originId, "card");
        });
    }
}

// ═══════════════════════════════════════════════════════════
// Module D: Upgrade source tracking
// ═══════════════════════════════════════════════════════════

[HarmonyPatch]
public static class CardUpgradeTrackerPatch
{
    private static decimal _preDamage;
    private static decimal _preBlock;
    private static int _cardHash;

    /// <summary>
    /// Before a card is upgraded, capture its current damage/block values.
    /// </summary>
    [HarmonyPatch(typeof(CardCmd), nameof(CardCmd.Upgrade),
        new Type[] { typeof(CardModel), typeof(CardPreviewStyle) })]
    [HarmonyPrefix]
    public static void BeforeUpgrade(CardModel card)
    {
        Safe.Run(() =>
        {
            _cardHash = card.GetHashCode();
            var dv = card.DynamicVars;
            _preDamage = dv != null && dv.TryGetValue("Damage", out var dmg) ? dmg.BaseValue : 0;
            _preBlock = dv != null && dv.TryGetValue("Block", out var blk) ? blk.BaseValue : 0;
        });
    }

    /// <summary>
    /// After upgrade, compute delta and record upgrade source.
    /// </summary>
    [HarmonyPatch(typeof(CardCmd), nameof(CardCmd.Upgrade),
        new Type[] { typeof(CardModel), typeof(CardPreviewStyle) })]
    [HarmonyPostfix]
    public static void AfterUpgrade(CardModel card)
    {
        Safe.Run(() =>
        {
            if (card.GetHashCode() != _cardHash) return;

            var dv = card.DynamicVars;
            decimal postDamage = dv != null && dv.TryGetValue("Damage", out var dmg) ? dmg.BaseValue : 0;
            decimal postBlock = dv != null && dv.TryGetValue("Block", out var blk) ? blk.BaseValue : 0;

            int damageDelta = (int)(postDamage - _preDamage);
            int blockDelta = (int)(postBlock - _preBlock);

            if (damageDelta <= 0 && blockDelta <= 0) return;

            // Store the delta for later attribution when the upgraded card is played.
            // Source = the card that triggered the upgrade (e.g., Armaments), or "upgrade" fallback.
            // Fix D: also capture the upgrader's origin so potion-generated vs deck-native
            // upgraders (e.g. SKILL_POTION Armaments vs deck Armaments) attribute to distinct buckets.
            var tracker = CombatTracker.Instance;
            string sourceId = tracker.ActiveCardId ?? "upgrade";
            string sourceType = tracker.ActiveCardId != null ? "card" : "upgrade";
            string? upgraderOrigin = tracker.ActiveCardId != null ? tracker.ActiveCardOrigin : null;
            ContributionMap.Instance.RecordUpgradeDelta(card.GetHashCode(),
                damageDelta, blockDelta, sourceId, sourceType, upgraderOrigin);
        });
    }
}

// ═══════════════════════════════════════════════════════════
// Stars gained tracking
// Patches PlayerCmd.GainStars to record StarsContribution.
// ═══════════════════════════════════════════════════════════

[HarmonyPatch]
public static class StarsGainedPatch
{
    [HarmonyPatch(typeof(PlayerCmd), nameof(PlayerCmd.GainStars))]
    [HarmonyPostfix]
    public static void AfterGainStars(decimal amount)
    {
        Safe.Run(() => CombatTracker.Instance.OnStarsGained((int)amount));
    }
}

// ═══════════════════════════════════════════════════════════
// Existing patches: Buffer, Intangible, Potion
// ═══════════════════════════════════════════════════════════

[HarmonyPatch]
public static class PowerMitigationPatch
{
    [HarmonyPatch(typeof(BufferPower), nameof(BufferPower.ModifyHpLostAfterOstyLate))]
    [HarmonyPrefix]
    public static void BeforeBufferModify(Creature target, decimal amount, out decimal __state)
    {
        __state = amount;
    }

    [HarmonyPatch(typeof(BufferPower), nameof(BufferPower.ModifyHpLostAfterOstyLate))]
    [HarmonyPostfix]
    public static void AfterBufferModify(decimal __result, decimal __state, Creature target)
    {
        Safe.Run(() =>
        {
            if (__state > 0 && __result == 0 && target.IsPlayer)
            {
                CombatTracker.Instance.OnBufferPrevention((int)__state);
            }
        });
    }

    [HarmonyPatch(typeof(IntangiblePower), nameof(IntangiblePower.ModifyHpLostAfterOsty))]
    [HarmonyPrefix]
    public static void BeforeIntangibleModify(Creature target, decimal amount, out decimal __state)
    {
        __state = amount;
    }

    [HarmonyPatch(typeof(IntangiblePower), nameof(IntangiblePower.ModifyHpLostAfterOsty))]
    [HarmonyPostfix]
    public static void AfterIntangibleModify(decimal __result, decimal __state, Creature target)
    {
        Safe.Run(() =>
        {
            if (__state > __result && __result >= 0 && target.IsPlayer)
            {
                CombatTracker.Instance.OnIntangibleReduction((int)__state, (int)__result);
            }
        });
    }

    // Round 14 v5 review §12: HardenedShell mitigates incoming damage via
    // ModifyHpLostBeforeOstyLate (Min(amount, shellRemaining)). Pattern mirrors
    // BufferPower — compute the absorbed delta as state->result, route to
    // MitigatedByBuff via a new helper on CombatTracker.
    [HarmonyPatch(typeof(HardenedShellPower), nameof(HardenedShellPower.ModifyHpLostBeforeOstyLate))]
    [HarmonyPrefix]
    public static void BeforeHardenedShellModify(Creature target, decimal amount, out decimal __state)
    {
        __state = amount;
    }

    [HarmonyPatch(typeof(HardenedShellPower), nameof(HardenedShellPower.ModifyHpLostBeforeOstyLate))]
    [HarmonyPostfix]
    public static void AfterHardenedShellModify(decimal __result, decimal __state, Creature target)
    {
        Safe.Run(() =>
        {
            if (__state > __result && __result >= 0 && target.IsPlayer)
            {
                int prevented = (int)(__state - __result);
                CombatTracker.Instance.OnHardenedShellMitigation(prevented);
            }
        });
    }
}

[HarmonyPatch]
public static class PotionUsedPatch
{
    [HarmonyPatch(typeof(CombatHistory), nameof(CombatHistory.PotionUsed))]
    [HarmonyPostfix]
    public static void AfterPotionUsed(CombatState combatState, PotionModel potion, Creature? target)
    {
        Safe.Run(() =>
        {
            var potionId = potion?.Id.Entry;
            if (!string.IsNullOrEmpty(potionId))
            {
                Safe.Info($"[DIAG:Potion] Used: {potionId}");
            }
            // Clear potion context here — this sync method fires AFTER OnUse() completes
            // inside the async OnUseWrapper chain (line 258 of PotionModel.cs).
            CombatTracker.Instance.ClearActivePotion();

            // Round 9 round 32: real-time UI refresh after potion use, mirroring
            // CardPlayFinished. Without this, F8 panel stayed stale until the
            // next card play.
            CombatTracker.Instance.NotifyCombatDataUpdated();
        });
    }
}

[HarmonyPatch]
public static class PotionContextPatch
{
    // Only Prefix — do NOT add a Postfix here.
    // OnUseWrapper is async: a Postfix would fire at the first await (before OnUse()),
    // clearing _activePotionId before effects execute.
    // Clearing happens in PotionUsedPatch.AfterPotionUsed (sync, fires after OnUse).
    [HarmonyPatch(typeof(PotionModel), nameof(PotionModel.OnUseWrapper))]
    [HarmonyPrefix]
    public static void BeforePotionUse(PotionModel __instance)
    {
        Safe.Run(() =>
        {
            var potionId = __instance.Id.Entry;
            if (!string.IsNullOrEmpty(potionId))
            {
                CombatTracker.Instance.SetActivePotion(potionId);
            }
        });
    }
}

// ═══════════════════════════════════════════════════════════
// Healing tracking
// Patches CreatureCmd.Heal to record HP healed with source attribution.
// ═══════════════════════════════════════════════════════════

[HarmonyPatch]
public static class HealingPatch
{
    [HarmonyPatch(typeof(CreatureCmd), nameof(CreatureCmd.Heal),
        new Type[] { typeof(Creature), typeof(decimal), typeof(bool) })]
    [HarmonyPrefix]
    public static void BeforeHeal(Creature creature, decimal amount, out (int hash, decimal hpBefore) __state)
    {
        __state = (0, 0);
        try
        {
            if (creature != null && creature.IsPlayer)
            {
                __state = (creature.GetHashCode(), creature.CurrentHp);
            }
        }
        catch { /* safe */ }
    }

    [HarmonyPatch(typeof(CreatureCmd), nameof(CreatureCmd.Heal),
        new Type[] { typeof(Creature), typeof(decimal), typeof(bool) })]
    [HarmonyPostfix]
    public static void AfterHeal(Creature creature, (int hash, decimal hpBefore) __state)
    {
        Safe.Run(() =>
        {
            if (creature == null || !creature.IsPlayer) return;
            if (__state.hash != creature.GetHashCode()) return;

            // NEW-3: Skip if this heal was triggered internally by GainMaxHp
            // (MaxHpGainPatch already recorded it as HpHealed)
            if (ContributionMap.Instance.CheckAndClearGainMaxHpFlag()) return;

            // Skip initial HP setup (character created with 0 HP then healed to full)
            if (__state.hpBefore <= 0) return;

            int actualHealed = (int)(creature.CurrentHp - __state.hpBefore);
            if (actualHealed <= 0) return;

            // Determine fallback source from room type when no active context exists
            string? fallbackId = null;
            string? fallbackType = null;
            var runState = creature.Player?.RunState;
            var room = runState?.CurrentRoom;
            int floor = runState?.TotalFloor ?? 0;

            if (room is RestSiteRoom)
            {
                fallbackId = "REST_SITE";
                fallbackType = "rest";
            }
            else if (room is EventRoom eventRoom)
            {
                // Use the specific event ID for per-event distinction
                var eventId = eventRoom.CanonicalEvent?.Id.Entry;
                fallbackId = !string.IsNullOrEmpty(eventId) ? eventId : "EVENT_UNKNOWN";
                fallbackType = "event";
            }
            else if (room is MerchantRoom)
            {
                fallbackId = "MERCHANT";
                fallbackType = "merchant";
            }
            else
            {
                // Between-floor recovery or other non-room healing
                fallbackId = $"FLOOR_{floor}_REGEN";
                fallbackType = "floor_regen";
            }

            CombatTracker.Instance.OnHealingReceived(actualHealed, fallbackId, fallbackType);
        });
    }
}

// ═══════════════════════════════════════════════════════════
// Killing blow damage capture
// CreatureCmd.Damage skips CombatHistory.DamageReceived when IsEnding is true
// (last primary enemy killed). Hook.AfterDamageGiven (line 265) is still called
// regardless. We manually patch it to capture the missed damage.
// Uses identity hash dedup to avoid double-counting results that DamageReceived
// already processed (for multi-target attacks where only the last target's hit
// was skipped).
// ═══════════════════════════════════════════════════════════

public static class KillingBlowPatcher
{
    // NOTE: Parameter names MUST match the compiled DLL, not the decompiled source.
    // Compiled: results (not damageResult), target (not originalTarget)
    public static void CaptureKillingBlow(
        CombatState combatState,
        Creature? dealer,
        DamageResult results,
        Creature target,
        CardModel? cardSource)
    {
        Safe.Run(() =>
        {
            // Skip if DamageReceived already handled this result
            int resultId = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(results);
            if (CombatTracker.Instance.IsDamageResultProcessed(resultId)) return;

            var receiver = results.Receiver;
            bool isPlayerReceiver = receiver.IsPlayer;

            // Only care about damage TO enemies
            if (isPlayerReceiver) return;
            if (results.TotalDamage <= 0) return;

            var cardSourceId = cardSource?.Id.Entry;

            Safe.Info($"[KillingBlow] Capturing missed damage: {cardSourceId ?? "?"} → {results.TotalDamage} to {target}");

            // Invincible-phase guard — same reasoning as AfterDamageReceived.
            bool invincible = receiver.ShowsInfiniteHp;

            CombatTracker.Instance.OnDamageDealt(
                results.TotalDamage,
                results.BlockedDamage,
                cardSourceId,
                isPlayerReceiver,
                receiver.GetHashCode(),
                dealer?.GetHashCode() ?? 0,
                skipEnemyDamageAttribution: invincible);
        });
    }

    public static void PatchAll(Harmony harmony)
    {
        try
        {
            var targetMethod = AccessTools.Method(typeof(Hook), nameof(Hook.AfterDamageGiven));
            if (targetMethod == null)
            {
                Safe.Warn("[KillingBlow] Hook.AfterDamageGiven method not found!");
                return;
            }
            var prefix = new HarmonyMethod(AccessTools.Method(typeof(KillingBlowPatcher), nameof(CaptureKillingBlow)));
            harmony.Patch(targetMethod, prefix: prefix);
            Safe.Info("[KillingBlow] Successfully patched Hook.AfterDamageGiven");
        }
        catch (Exception ex)
        {
            Safe.Warn($"[KillingBlow] Failed to patch: {ex}");
        }
    }
}

// NOTE: RegentPowerHookContextPatch removed — consolidated into PowerHookContextPatcher.

// ═══════════════════════════════════════════════════════════
// ═══════════════════════════════════════════════════════════
// Forge tracking: records each ForgeCmd.Forge call with its source
// so SovereignBlade sub-bars can show per-source forge breakdown.
// ═══════════════════════════════════════════════════════════

[HarmonyPatch]
public static class ForgeCmdPatch
{
    [HarmonyPatch(typeof(ForgeCmd), nameof(ForgeCmd.Forge))]
    [HarmonyPostfix]
    public static void AfterForge(decimal amount, AbstractModel? source)
    {
        Safe.Run(() =>
        {
            if (amount <= 0 || source == null) return;
            string sourceId = source.Id.Entry;
            if (string.IsNullOrEmpty(sourceId)) return;
            // P2-2: Distinguish PowerModel forges from card/relic forges so
            // FlushForgeSubBars can normalize FurnacePower/BulwarkPower IDs.
            string sourceType;
            if (source is RelicModel) sourceType = "relic";
            else if (source is PowerModel) sourceType = "power";
            else sourceType = "card";
            CombatTracker.Instance.OnForge(sourceId, sourceType, (int)amount);
        });
    }
}

// SovereignBlade + SeekingEdge damage split
// When SeekingEdge is active, records the primary target (highest HP enemy)
// so that non-primary damage is attributed to SeekingEdge's source card.
// ═══════════════════════════════════════════════════════════

[HarmonyPatch]
public static class SovereignBladeSeekingEdgePatch
{
    [HarmonyPatch(typeof(SovereignBlade), "OnPlay")]
    [HarmonyPrefix]
    public static void BeforeSovereignBlade(SovereignBlade __instance)
    {
        Safe.Run(() =>
        {
            // Flush forge contributions as sub-bars under SOVEREIGN_BLADE
            CombatTracker.Instance.FlushForgeSubBars();

            if (!__instance.Owner.Creature.HasPower<SeekingEdgePower>()) return;
            var enemies = __instance.CombatState?.HittableEnemies;
            if (enemies == null || enemies.Count == 0) return;

            Creature? primary = null;
            decimal maxHp = -1;
            foreach (var e in enemies)
            {
                if (e.CurrentHp > maxHp)
                {
                    maxHp = e.CurrentHp;
                    primary = e;
                }
            }
            if (primary != null)
                CombatTracker.Instance.SetSeekingEdgeContext(primary.GetHashCode());
        });
    }

    [HarmonyPatch(typeof(SovereignBlade), "OnPlay")]
    [HarmonyPostfix]
    public static void AfterSovereignBlade()
    {
        Safe.Run(() => CombatTracker.Instance.ClearSeekingEdgeContext());
    }
}

// ═══════════════════════════════════════════════════════════
// NEW-1/NEW-2: Cost Reduction Source Tagging
// Layer 1: Records WHICH source reduced a card's cost (tag only, no amounts).
// Layer 2: CardPlayStarted postfix computes actual savings at play time.
// ═══════════════════════════════════════════════════════════

[HarmonyPatch]
public static class CostReductionSourceTagPatch
{
    // ── Generic helpers ──

    private static void TagPowerReduction(bool result, AbstractModel instance, CardModel card,
        decimal originalCost, decimal modifiedCost)
    {
        if (!result || originalCost <= modifiedCost) return;
        var powerId = instance.Id.Entry;
        var source = ContributionMap.Instance.GetPowerSource(powerId);
        string srcId = source?.SourceId ?? powerId;
        string srcType = source?.SourceType ?? "power";
        ContributionMap.Instance.TagCostReductionSource(card.GetHashCode(), srcId, srcType);
    }

    private static void TagRelicReduction(bool result, AbstractModel instance, CardModel card,
        decimal originalCost, decimal modifiedCost)
    {
        if (!result || originalCost <= modifiedCost) return;
        ContributionMap.Instance.TagCostReductionSource(
            card.GetHashCode(), instance.Id.Entry, "relic");
    }

    // ── Hook-based energy reduction tagging ──

    [HarmonyPatch(typeof(VoidFormPower), nameof(VoidFormPower.TryModifyEnergyCostInCombat))]
    [HarmonyPostfix]
    public static void AfterVoidFormEnergy(bool __result, VoidFormPower __instance,
        CardModel card, decimal originalCost, decimal modifiedCost)
    {
        Safe.Run(() => TagPowerReduction(__result, __instance, card, originalCost, modifiedCost));
    }

    [HarmonyPatch(typeof(FreeAttackPower), nameof(FreeAttackPower.TryModifyEnergyCostInCombat))]
    [HarmonyPostfix]
    public static void AfterFreeAttackEnergy(bool __result, FreeAttackPower __instance,
        CardModel card, decimal originalCost, decimal modifiedCost)
    {
        Safe.Run(() => TagPowerReduction(__result, __instance, card, originalCost, modifiedCost));
    }

    [HarmonyPatch(typeof(FreeSkillPower), nameof(FreeSkillPower.TryModifyEnergyCostInCombat))]
    [HarmonyPostfix]
    public static void AfterFreeSkillEnergy(bool __result, FreeSkillPower __instance,
        CardModel card, decimal originalCost, decimal modifiedCost)
    {
        Safe.Run(() => TagPowerReduction(__result, __instance, card, originalCost, modifiedCost));
    }

    [HarmonyPatch(typeof(FreePowerPower), nameof(FreePowerPower.TryModifyEnergyCostInCombat))]
    [HarmonyPostfix]
    public static void AfterFreePowerEnergy(bool __result, FreePowerPower __instance,
        CardModel card, decimal originalCost, decimal modifiedCost)
    {
        Safe.Run(() => TagPowerReduction(__result, __instance, card, originalCost, modifiedCost));
    }

    [HarmonyPatch(typeof(CorruptionPower), nameof(CorruptionPower.TryModifyEnergyCostInCombat))]
    [HarmonyPostfix]
    public static void AfterCorruptionEnergy(bool __result, CorruptionPower __instance,
        CardModel card, decimal originalCost, decimal modifiedCost)
    {
        Safe.Run(() => TagPowerReduction(__result, __instance, card, originalCost, modifiedCost));
    }

    [HarmonyPatch(typeof(VeilpiercerPower), nameof(VeilpiercerPower.TryModifyEnergyCostInCombat))]
    [HarmonyPostfix]
    public static void AfterVeilpiercerEnergy(bool __result, VeilpiercerPower __instance,
        CardModel card, decimal originalCost, decimal modifiedCost)
    {
        Safe.Run(() => TagPowerReduction(__result, __instance, card, originalCost, modifiedCost));
    }

    [HarmonyPatch(typeof(CuriousPower), nameof(CuriousPower.TryModifyEnergyCostInCombat))]
    [HarmonyPostfix]
    public static void AfterCuriousEnergy(bool __result, CuriousPower __instance,
        CardModel card, decimal originalCost, decimal modifiedCost)
    {
        Safe.Run(() => TagPowerReduction(__result, __instance, card, originalCost, modifiedCost));
    }

    // ── Hook-based star reduction tagging ──

    [HarmonyPatch(typeof(VoidFormPower), nameof(VoidFormPower.TryModifyStarCost))]
    [HarmonyPostfix]
    public static void AfterVoidFormStar(bool __result, VoidFormPower __instance,
        CardModel card, decimal originalCost, decimal modifiedCost)
    {
        Safe.Run(() => TagPowerReduction(__result, __instance, card, originalCost, modifiedCost));
    }
}

// ═══════════════════════════════════════════════════════════
// NEW-1: Local cost modifier source tagging
// Patches SetToFreeThisTurn, SetToFreeThisCombat to record which source
// made a card free via local modifiers (BulletTime, potions, relics, etc.)
// ═══════════════════════════════════════════════════════════

[HarmonyPatch]
public static class LocalCostModifierSourceTagPatch
{
    [HarmonyPatch(typeof(CardModel), nameof(CardModel.SetToFreeThisTurn))]
    [HarmonyPrefix]
    public static void BeforeSetFreeThisTurn(CardModel __instance)
    {
        Safe.Run(() => TagLocalCostReduction(__instance));
    }

    [HarmonyPatch(typeof(CardModel), nameof(CardModel.SetToFreeThisCombat))]
    [HarmonyPrefix]
    public static void BeforeSetFreeThisCombat(CardModel __instance)
    {
        Safe.Run(() => TagLocalCostReduction(__instance));
    }

    private static void TagLocalCostReduction(CardModel card)
    {
        int baseCost = card.EnergyCost.Canonical;
        if (baseCost <= 0) return; // already free by default

        var tracker = CombatTracker.Instance;

        string sourceId;
        string sourceType;

        // Fix 3.6: power > relic > potion > card priority
        if (tracker.ActivePowerSourceId != null)
        {
            sourceId = tracker.ActivePowerSourceId;
            sourceType = tracker.ActivePowerSourceType ?? "power";
        }
        else if (tracker.ActiveRelicId != null)
        {
            sourceId = tracker.ActiveRelicId;
            sourceType = "relic";
        }
        else if (tracker.ActivePotionId != null)
        {
            sourceId = tracker.ActivePotionId;
            sourceType = "potion";
        }
        else if (tracker.ActiveCardId != null)
        {
            sourceId = tracker.ActiveCardId;
            sourceType = "card";
        }
        else
        {
            return; // no context — can't attribute
        }

        // Exception rule: if source ALSO generated this card, mark as generated-and-free
        // (e.g., Attack Potion generates a card AND makes it free — only sub-bar, no energy credit)
        var origin = ContributionMap.Instance.GetCardOrigin(card.GetHashCode());
        if (origin != null && origin.Value.originId == sourceId)
        {
            ContributionMap.Instance.MarkCardAsGeneratedAndFree(card.GetHashCode());
            return;
        }

        ContributionMap.Instance.TagCostReductionSource(
            card.GetHashCode(), sourceId, sourceType);
    }
}

// ═══════════════════════════════════════════════════════════
// PRD-04 §4.2 — Confused/SneckoEye energy contribution (negative-allowed).
// ConfusedPower.AfterCardDrawn → SetThisCombat → randomized cost.
// We tag the card with the upstream Snecko source so AttributeCostSavings
// credits the energy delta (which can be negative) at play time.
// ═══════════════════════════════════════════════════════════

[HarmonyPatch]
public static class ConfusedSourceTagPatch
{
    [HarmonyPatch(typeof(ConfusedPower), nameof(ConfusedPower.AfterCardDrawn))]
    [HarmonyPostfix]
    public static void AfterConfusedRandomizesCost(
        ConfusedPower __instance, CardModel card)
    {
        Safe.Run(() =>
        {
            if (card == null || card.Owner != __instance.Owner.Player) return;
            if (card.EnergyCost.Canonical < 0) return;

            // Identify the relic that gave the player Confused.
            // Order matters: check FakeSneckoEye first (more specific), then SneckoEye.
            string sourceId;
            try
            {
                var player = __instance.Owner.Player;
                if (player == null) return;
                if (player.GetRelic<FakeSneckoEye>() != null)      sourceId = "FAKE_SNECKO_EYE";
                else if (player.GetRelic<SneckoEye>() != null)     sourceId = "SNECKO_EYE";
                else return; // Confused from some other source — skip
            }
            catch (System.Exception ex)
            {
                Safe.Warn($"ConfusedSourceTagPatch: relic lookup failed: {ex.Message}");
                return;
            }

            ContributionMap.Instance.TagCostReductionSource(
                card.GetHashCode(), sourceId, "relic");
        });
    }
}

// ═══════════════════════════════════════════════════════════
// PRD-04 §4.2 (SneckoOil) — local-cost modifier path that bypasses
// CardModel.SetToFreeThis*. SneckoOil iterates each hand card and calls
// CardEnergyCost.SetThisTurnOrUntilPlayed directly, so we hook the
// underlying setter and pick up the active potion / relic context.
// PRD-04 §4.3 (Enlightenment partial cost reduction) — Enlightenment
// rewrites every hand card to cost 1 via SetThisTurnOrUntilPlayed; this
// patch credits the delta when the card is later played.
// ═══════════════════════════════════════════════════════════

[HarmonyPatch]
public static class EnergyCostSetterTagPatch
{
    [HarmonyPatch(typeof(CardEnergyCost), nameof(CardEnergyCost.SetThisTurnOrUntilPlayed))]
    [HarmonyPostfix]
    public static void AfterSetThisTurnOrUntilPlayed(CardEnergyCost __instance)
    {
        Safe.Run(() => TagFromActiveContext(__instance));
    }

    [HarmonyPatch(typeof(CardEnergyCost), nameof(CardEnergyCost.SetThisCombat))]
    [HarmonyPostfix]
    public static void AfterSetThisCombat(CardEnergyCost __instance)
    {
        Safe.Run(() => TagFromActiveContext(__instance));
    }

    private static void TagFromActiveContext(CardEnergyCost energyCost)
    {
        // Read the private CardEnergyCost._card field via Traverse
        CardModel? card;
        try
        {
            card = Traverse.Create(energyCost).Field("_card").GetValue<CardModel>();
        }
        catch
        {
            return;
        }
        if (card == null || card.EnergyCost.Canonical < 0) return;

        // If something already tagged this card (e.g. ConfusedSourceTagPatch),
        // don't overwrite — first tagger wins for the same card hash.
        var existing = ContributionMap.Instance.GetCostReductionSourceTag(card.GetHashCode());
        if (existing != null) return;

        // Fix 3.6: power > relic > potion > card priority
        var tracker = CombatTracker.Instance;
        string sourceId;
        string sourceType;
        if (tracker.ActivePowerSourceId != null) { sourceId = tracker.ActivePowerSourceId; sourceType = tracker.ActivePowerSourceType ?? "power"; }
        else if (tracker.ActiveRelicId != null) { sourceId = tracker.ActiveRelicId; sourceType = "relic"; }
        else if (tracker.ActivePotionId != null) { sourceId = tracker.ActivePotionId; sourceType = "potion"; }
        else if (tracker.ActiveCardId != null) { sourceId = tracker.ActiveCardId; sourceType = "card"; }
        else return;

        // Generate-and-free exception (same as LocalCostModifierSourceTagPatch).
        var origin = ContributionMap.Instance.GetCardOrigin(card.GetHashCode());
        if (origin != null && origin.Value.originId == sourceId)
        {
            ContributionMap.Instance.MarkCardAsGeneratedAndFree(card.GetHashCode());
            return;
        }

        ContributionMap.Instance.TagCostReductionSource(
            card.GetHashCode(), sourceId, sourceType);
    }
}

// ═══════════════════════════════════════════════════════════
// NEW-3: Max HP Gain as Healing
// GainMaxHp increases are recorded as HpHealed.
// Uses context flag to suppress the internal Heal call from double-counting.
// ═══════════════════════════════════════════════════════════

[HarmonyPatch]
public static class MaxHpGainPatch
{
    [HarmonyPatch(typeof(CreatureCmd), nameof(CreatureCmd.GainMaxHp),
        new Type[] { typeof(Creature), typeof(decimal) })]
    [HarmonyPrefix]
    public static void BeforeGainMaxHp(Creature creature, decimal amount, out int __state)
    {
        __state = 0;
        try
        {
            if (creature != null && creature.IsPlayer && amount > 0m)
            {
                __state = creature.MaxHp;
                // Flag tells HealingPatch to skip the internal Heal from GainMaxHp
                ContributionMap.Instance.SetGainMaxHpFlag(true);
            }
        }
        catch { /* safe */ }
    }

    [HarmonyPatch(typeof(CreatureCmd), nameof(CreatureCmd.GainMaxHp),
        new Type[] { typeof(Creature), typeof(decimal) })]
    [HarmonyPostfix]
    public static void AfterGainMaxHp(Creature creature, int __state)
    {
        Safe.Run(() =>
        {
            if (creature == null || !creature.IsPlayer || __state == 0) return;

            int actualGain = creature.MaxHp - __state;
            if (actualGain <= 0)
            {
                ContributionMap.Instance.SetGainMaxHpFlag(false);
                return;
            }
            // Note: do NOT clear the flag here — it must stay set until
            // HealingPatch sees and clears it (GainMaxHp calls Heal AFTER this Postfix)

            // Determine source context
            string? fallbackId = null;
            string? fallbackType = null;

            var tracker = CombatTracker.Instance;
            if (tracker.ActiveRelicId != null)
            {
                fallbackId = tracker.ActiveRelicId;
                fallbackType = "relic";
            }
            else
            {
                var room = creature.Player?.RunState?.CurrentRoom;
                if (room is EventRoom)
                {
                    fallbackId = "EVENT";
                    fallbackType = "event";
                }
                else if (room is RestSiteRoom)
                {
                    fallbackId = "REST_SITE";
                    fallbackType = "rest_site";
                }
            }

            // Record max HP gain as healing contribution
            CombatTracker.Instance.OnHealingReceived(actualGain, fallbackId, fallbackType);
        });
    }
}

// NEW-3 heal suppression is handled directly in AfterHeal (HealingPatch) above,
// using ContributionMap.CheckAndClearGainMaxHpFlag().

// ═══════════════════════════════════════════════════════════
// ModifyHandDraw extra draw tracking (PaleBlueDot, Tyranny)
// Records extra draws from powers that modify hand draw count.
// ═══════════════════════════════════════════════════════════

[HarmonyPatch]
public static class HandDrawBonusPatch
{
    [HarmonyPatch(typeof(PaleBlueDotPower), nameof(PaleBlueDotPower.ModifyHandDraw))]
    [HarmonyPostfix]
    public static void AfterPaleBlueDotDraw(decimal __result, PaleBlueDotPower __instance,
        Player player, decimal count)
    {
        Safe.Run(() =>
        {
            if (player != __instance.Owner.Player) return;
            int extra = (int)(__result - count);
            if (extra <= 0) return;
            var source = ContributionMap.Instance.GetPowerSource(__instance.Id.Entry);
            if (source != null)
                ContributionMap.Instance.RecordHandDrawBonus(source.SourceId, source.SourceType, extra);
        });
    }

    [HarmonyPatch(typeof(TyrannyPower), nameof(TyrannyPower.ModifyHandDraw))]
    [HarmonyPostfix]
    public static void AfterTyrannyDraw(decimal __result, TyrannyPower __instance,
        Player player, decimal count)
    {
        Safe.Run(() =>
        {
            if (player != __instance.Owner.Player) return;
            int extra = (int)(__result - count);
            if (extra <= 0) return;
            var source = ContributionMap.Instance.GetPowerSource(__instance.Id.Entry);
            if (source != null)
                ContributionMap.Instance.RecordHandDrawBonus(source.SourceId, source.SourceType, extra);
        });
    }

    [HarmonyPatch(typeof(DemesnePower), nameof(DemesnePower.ModifyHandDraw))]
    [HarmonyPostfix]
    public static void AfterDemesneDraw(decimal __result, DemesnePower __instance,
        Player player, decimal count)
    {
        Safe.Run(() =>
        {
            if (player != __instance.Owner.Player) return;
            int extra = (int)(__result - count);
            if (extra <= 0) return;
            var source = ContributionMap.Instance.GetPowerSource(__instance.Id.Entry);
            if (source != null)
                ContributionMap.Instance.RecordHandDrawBonus(source.SourceId, source.SourceType, extra);
        });
    }

    // Fix 3.4 v2 (Round 14 v5): Demesne/Friendship's ModifyMaxEnergy is a
    // PROPERTY GETTER (PlayerCombatState.MaxEnergy => Hook.ModifyMaxEnergy(...)),
    // which fires on EVERY read — UI refresh, card cost check, AI decision, etc.
    // Postfixing ModifyMaxEnergy inflated EnergyGained by a large multiplier
    // (once per read, not once per turn).
    //
    // Correct attribution point: Hook.AfterEnergyReset, which fires exactly
    // once per player turn after energy is reset. Iterate player's powers,
    // find each one that contributes to MaxEnergy, and credit its source card
    // by its Amount. Centralized so adding new energy-bonus powers is trivial.
    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterEnergyReset))]
    [HarmonyPostfix]
    public static void AfterEnergyResetAttribution(CombatState combatState, Player player)
    {
        Safe.Run(() =>
        {
            if (player?.Creature == null) return;
            foreach (var power in player.Creature.Powers)
            {
                int bonus = 0;
                string? powerId = null;
                if (power is DemesnePower demesne)
                {
                    bonus = (int)demesne.Amount;
                    powerId = demesne.Id.Entry;
                }
                else if (power is FriendshipPower friendship)
                {
                    bonus = (int)friendship.Amount;
                    powerId = friendship.Id.Entry;
                }
                else if (power is PyrePower pyre)
                {
                    // Round 14 v5 review §5: Pyre is a Regent modifier-only
                    // power (ModifyMaxEnergy override) like Demesne/Friendship.
                    // Without this branch, its energy contribution is lost.
                    bonus = (int)pyre.Amount;
                    powerId = pyre.Id.Entry;
                }
                if (bonus <= 0 || powerId == null) continue;

                var source = ContributionMap.Instance.GetPowerSource(powerId);
                if (source != null)
                    CombatTracker.Instance.AddEnergyBonusDirect(source.SourceId, source.SourceType, bonus);
                else
                    // Fallback when no source mapping exists (e.g., power applied outside
                    // tracked play): credit under the power's own ID.
                    CombatTracker.Instance.AddEnergyBonusDirect(powerId, "power", bonus);
            }

            // Round 15: relics with ModifyMaxEnergy override (SOZU, ECTOPLASM,
            // PRISMATIC_GEM, SPIKED_GAUNTLETS, WHISPERING_EARRING,
            // BLOOD_SOAKED_ROSE, PUMPKIN_CANDLE). Call the virtual
            // ModifyMaxEnergy(player, 0) — relics that override it return
            // (0 + bonus) = bonus; the default base implementation just
            // returns amount (0). This lets us pick up any current or future
            // relic that adds to max energy without a hardcoded list, and
            // also correctly respects PumpkinCandle's act-guard (it returns 0
            // when on the wrong act).
            foreach (var relic in player.Relics)
            {
                if (relic == null) continue;
                int relicBonus;
                try { relicBonus = (int)relic.ModifyMaxEnergy(player, 0m); }
                catch { continue; }
                if (relicBonus <= 0) continue;

                var relicId = relic.Id.Entry;
                if (string.IsNullOrEmpty(relicId)) continue;
                CombatTracker.Instance.AddEnergyBonusDirect(relicId, "relic", relicBonus);
            }
        });
    }

    [HarmonyPatch(typeof(MachineLearningPower), nameof(MachineLearningPower.ModifyHandDraw))]
    [HarmonyPostfix]
    public static void AfterMachineLearningDraw(decimal __result, MachineLearningPower __instance,
        Player player, decimal count)
    {
        Safe.Run(() =>
        {
            if (player != __instance.Owner.Player) return;
            int extra = (int)(__result - count);
            if (extra <= 0) return;
            var source = ContributionMap.Instance.GetPowerSource(__instance.Id.Entry);
            if (source != null)
                ContributionMap.Instance.RecordHandDrawBonus(source.SourceId, source.SourceType, extra);
        });
    }

    [HarmonyPatch(typeof(ToolsOfTheTradePower), nameof(ToolsOfTheTradePower.ModifyHandDraw))]
    [HarmonyPostfix]
    public static void AfterToolsOfTheTradeDraw(decimal __result, ToolsOfTheTradePower __instance,
        Player player, decimal count)
    {
        Safe.Run(() =>
        {
            if (player != __instance.Owner.Player) return;
            int extra = (int)(__result - count);
            if (extra <= 0) return;
            var source = ContributionMap.Instance.GetPowerSource(__instance.Id.Entry);
            if (source != null)
                ContributionMap.Instance.RecordHandDrawBonus(source.SourceId, source.SourceType, extra);
        });
    }

    [HarmonyPatch(typeof(DrawCardsNextTurnPower), nameof(DrawCardsNextTurnPower.ModifyHandDraw))]
    [HarmonyPostfix]
    public static void AfterDrawCardsNextTurnDraw(decimal __result, DrawCardsNextTurnPower __instance,
        Player player, decimal count)
    {
        Safe.Run(() =>
        {
            if (player != __instance.Owner.Player) return;
            int extra = (int)(__result - count);
            if (extra <= 0) return;
            var source = ContributionMap.Instance.GetPowerSource(__instance.Id.Entry);
            if (source != null)
                ContributionMap.Instance.RecordHandDrawBonus(source.SourceId, source.SourceType, extra);
        });
    }

    // Flush pending hand draw bonuses after hand draw modifiers complete
    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterModifyingHandDraw))]
    [HarmonyPostfix]
    public static void AfterModifyingHandDraw()
    {
        Safe.Run(() => CombatTracker.Instance.FlushPendingHandDrawBonus());
    }
}

// ═══════════════════════════════════════════════════════════
// Relic ModifyHandDraw extra draw tracking
// Records extra draws from relics that modify hand draw count.
// ═══════════════════════════════════════════════════════════

[HarmonyPatch]
public static class RelicHandDrawBonusPatch
{
    private static void RecordRelicDrawBonus(RelicModel relic, decimal result, decimal count)
    {
        Safe.Run(() =>
        {
            int extra = (int)(result - count);
            if (extra <= 0) return;
            var relicId = relic.Id.Entry;
            if (!string.IsNullOrEmpty(relicId))
                ContributionMap.Instance.RecordHandDrawBonus(relicId, "relic", extra);
        });
    }

    [HarmonyPatch(typeof(BagOfPreparation), nameof(BagOfPreparation.ModifyHandDraw))]
    [HarmonyPostfix]
    public static void AfterBagOfPreparation(decimal __result, BagOfPreparation __instance,
        Player player, decimal count)
    {
        if (player == __instance.Owner) RecordRelicDrawBonus(__instance, __result, count);
    }

    [HarmonyPatch(typeof(Pocketwatch), nameof(Pocketwatch.ModifyHandDraw))]
    [HarmonyPostfix]
    public static void AfterPocketwatch(decimal __result, Pocketwatch __instance,
        Player player, decimal count)
    {
        if (player == __instance.Owner) RecordRelicDrawBonus(__instance, __result, count);
    }

    [HarmonyPatch(typeof(PaelsBlood), nameof(PaelsBlood.ModifyHandDraw))]
    [HarmonyPostfix]
    public static void AfterPaelsBlood(decimal __result, PaelsBlood __instance,
        Player player, decimal count)
    {
        if (player == __instance.Owner) RecordRelicDrawBonus(__instance, __result, count);
    }

    [HarmonyPatch(typeof(RingOfTheSnake), nameof(RingOfTheSnake.ModifyHandDraw))]
    [HarmonyPostfix]
    public static void AfterRingOfTheSnake(decimal __result, RingOfTheSnake __instance,
        Player player, decimal count)
    {
        if (player == __instance.Owner) RecordRelicDrawBonus(__instance, __result, count);
    }

    [HarmonyPatch(typeof(RingOfTheDrake), nameof(RingOfTheDrake.ModifyHandDraw))]
    [HarmonyPostfix]
    public static void AfterRingOfTheDrake(decimal __result, RingOfTheDrake __instance,
        Player player, decimal count)
    {
        if (player == __instance.Owner) RecordRelicDrawBonus(__instance, __result, count);
    }

    [HarmonyPatch(typeof(SneckoEye), nameof(SneckoEye.ModifyHandDraw))]
    [HarmonyPostfix]
    public static void AfterSneckoEye(decimal __result, SneckoEye __instance,
        Player player, decimal count)
    {
        if (player == __instance.Owner) RecordRelicDrawBonus(__instance, __result, count);
    }

    // ── Round 15: remaining relic draw modifiers (relics.md sweep) ──

    [HarmonyPatch(typeof(BoomingConch), nameof(BoomingConch.ModifyHandDraw))]
    [HarmonyPostfix]
    public static void AfterBoomingConch(decimal __result, BoomingConch __instance,
        Player player, decimal count)
    {
        if (player == __instance.Owner) RecordRelicDrawBonus(__instance, __result, count);
    }

    [HarmonyPatch(typeof(BigMushroom), nameof(BigMushroom.ModifyHandDraw))]
    [HarmonyPostfix]
    public static void AfterBigMushroom(decimal __result, BigMushroom __instance,
        Player player, decimal cardsToDraw)
    {
        if (player == __instance.Owner) RecordRelicDrawBonus(__instance, __result, cardsToDraw);
    }

    [HarmonyPatch(typeof(PollinousCore), nameof(PollinousCore.ModifyHandDraw))]
    [HarmonyPostfix]
    public static void AfterPollinousCore(decimal __result, PollinousCore __instance,
        Player player, decimal count)
    {
        if (player == __instance.Owner) RecordRelicDrawBonus(__instance, __result, count);
    }

    [HarmonyPatch(typeof(Fiddle), nameof(Fiddle.ModifyHandDrawLate))]
    [HarmonyPostfix]
    public static void AfterFiddle(decimal __result, Fiddle __instance,
        Player player, decimal count)
    {
        if (player == __instance.Owner) RecordRelicDrawBonus(__instance, __result, count);
    }
}

// ═══════════════════════════════════════════════════════════
// Enemy strength reduction tracking for defense attribution
// Detects when negative Strength is applied to enemies by player sources.
// ═══════════════════════════════════════════════════════════

[HarmonyPatch]
public static class EnemyStrReductionPatch
{
    // Tracks old Amount at SetAmount entry so we can compute the true delta in
    // the postfix. Moving from `ApplyInternal` postfix to `SetAmount` prefix/
    // postfix fixes three prior bugs:
    //   A. Old code used `|__instance.Amount|` (total) instead of the delta.
    //      For enemies with pre-existing Strength (e.g. boss with innate buff),
    //      Piercing Wail's -8 delta was recorded as `|new total|` which was
    //      wrong (could be smaller or larger than the actual reduction).
    //   B. ApplyInternal only fires for the FIRST power instance. Subsequent
    //      applies to the same non-instanced power go through
    //      PowerCmd.ModifyAmount, which does NOT call ApplyInternal —
    //      reductions to enemies that already had a StrengthPower were
    //      silently missed. SetAmount fires on both paths.
    //   C. TemporaryStrengthPower's "isTemporary" flag was set from the
    //      `__instance is TemporaryStrengthPower` check. But the actual silent
    //      StrengthPower apply is on a *StrengthPower* (not its TempStr
    //      wrapper), so the check always returned false and revert-on-turn-end
    //      never matched.  Fix: read the *caller's* PowerModel via stack
    //      inspection of ApplierContext (tempStrWrapper) — simpler workaround:
    //      always mark as temporary when the active source's CardModel /
    //      PotionModel / RelicModel declares a TempStr power (rare), or just
    //      accept that turn-end revert clears based on SetAmount sign.

    [HarmonyPatch(typeof(PowerModel), nameof(PowerModel.SetAmount))]
    [HarmonyPrefix]
    public static void BeforeSetAmount_StrTracking(PowerModel __instance, out int __state)
    {
        __state = __instance.Amount;
    }

    [HarmonyPatch(typeof(PowerModel), nameof(PowerModel.SetAmount))]
    [HarmonyPostfix]
    public static void AfterSetAmount_StrTracking(PowerModel __instance, int amount, int __state)
    {
        Safe.Run(() =>
        {
            if (__instance is not StrengthPower) return;
            var owner = __instance.Owner;
            if (owner == null || owner.IsPlayer) return;

            // Delta = new - old. Negative delta = reduction.
            int oldAmount = __state;
            int newAmount = __instance.Amount; // or `amount`; SetAmount just set it
            int delta = newAmount - oldAmount;
            if (delta >= 0)
            {
                // Positive delta on an enemy's StrengthPower means either a
                // TempStr revert at turn end or some unusual buff. If we are
                // inside a TempStrengthPower.AfterTurnEnd call (detected via
                // ContributionMap.RevertingTempStrSource AsyncLocal tag set
                // by TempStrengthRevertPatch.BeforeTempStrRevert), consume
                // from the EXACT matching source entry so permanent
                // reductions (Malaise etc.) are not eaten by LIFO when a
                // temp reduction (Piercing Wail) reverts. Fall back to LIFO
                // only when the tag is absent (rare: non-TempStr buff paths).
                if (delta > 0)
                {
                    var revertSource = ContributionMap.Instance.RevertingTempStrSource;
                    if (revertSource.HasValue)
                    {
                        ContributionMap.Instance.ConsumeReductionFromSource(
                            owner.GetHashCode(), revertSource.Value.SourceId, delta);
                    }
                    else
                    {
                        ContributionMap.Instance.ReduceRecordedStrReduction(
                            owner.GetHashCode(), delta);
                    }
                }
                return;
            }

            // Source attribution: power > relic > potion > card
            var tracker = CombatTracker.Instance;
            string? sourceId = null;
            string sourceType = "card";
            if (tracker.ActivePowerSourceId != null)
            {
                sourceId = tracker.ActivePowerSourceId;
                sourceType = tracker.ActivePowerSourceType ?? "card";
            }
            else if (tracker.ActiveRelicId != null)
            {
                sourceId = tracker.ActiveRelicId;
                sourceType = "relic";
            }
            else if (tracker.ActivePotionId != null)
            {
                sourceId = tracker.ActivePotionId;
                sourceType = "potion";
            }
            else if (tracker.ActiveCardId != null)
            {
                sourceId = tracker.ActiveCardId;
                sourceType = "card";
            }
            if (sourceId == null) return;

            int reductionAmount = -delta;  // |delta|, positive
            // isTemporary=false here; TemporaryStrengthPower's revert is detected
            // via the positive-delta branch above (ReduceRecordedStrReduction).
            ContributionMap.Instance.RecordStrengthReduction(
                owner.GetHashCode(), sourceId, sourceType, reductionAmount, isTemporary: false);

            Safe.Info($"[StrReduction] enemy={owner.GetHashCode()} src={sourceId} old={oldAmount} new={newAmount} delta={delta} recorded={reductionAmount}");
        });
    }
}

// ═══════════════════════════════════════════════════════════
// TemporaryStrengthPower revert — mark source around the revert window
//
// TemporaryStrengthPower.AfterTurnEnd fires on each player/enemy turn end;
// when `side == Owner.Side`, it `await PowerCmd.Apply<StrengthPower>(+Amount)`
// to undo the temp strength. That fires StrengthPower.SetAmount with delta
// > 0 which our EnemyStrReductionPatch postfix interprets as "revert,
// consume from _enemyStrReductions".
//
// Problem with plain LIFO consumption: if the list has entries from both
// a TempStr source (e.g. Piercing Wail -8) and an earlier permanent
// source (e.g. Malaise -2), LIFO eats the LAST entry first — which may
// be the permanent source, losing accurate credit.
//
// Fix: use an AsyncLocal source tag set by the prefix here. Harmony
// prefix runs synchronously before the async state machine body; the
// body captures the current ExecutionContext (including our AsyncLocal)
// at its first await. The captured EC flows through all nested awaits
// in the body, so when the inner Apply<StrengthPower> reaches SetAmount
// our AsyncLocal is still set. Postfix (fires at first-await sync
// return) clears the caller-side value, so unrelated SetAmount calls
// outside the revert window are not affected.
// ═══════════════════════════════════════════════════════════

[HarmonyPatch]
public static class TempStrengthRevertPatch
{
    [HarmonyPatch(typeof(TemporaryStrengthPower), nameof(TemporaryStrengthPower.AfterTurnEnd))]
    [HarmonyPrefix]
    public static void BeforeTempStrRevert(TemporaryStrengthPower __instance)
    {
        Safe.Run(() =>
        {
            if (__instance.Owner == null || __instance.Owner.IsPlayer) return;
            var origin = __instance.OriginModel;
            var id = origin?.Id.Entry;
            if (string.IsNullOrEmpty(id)) return;

            string type = origin switch
            {
                CardModel   => "card",
                RelicModel  => "relic",
                PotionModel => "potion",
                _           => "card",
            };
            ContributionMap.Instance.RevertingTempStrSource = (id!, type);
        });
    }

    [HarmonyPatch(typeof(TemporaryStrengthPower), nameof(TemporaryStrengthPower.AfterTurnEnd))]
    [HarmonyPostfix]
    public static void AfterTempStrRevert()
    {
        Safe.Run(() => ContributionMap.Instance.RevertingTempStrSource = null);
    }
}

// ═══════════════════════════════════════════════════════════
// Card generation origin tracking (SpectrumShift + general)
// Records origin for cards generated via CardPileCmd.AddGeneratedCardsToCombat.
// ═══════════════════════════════════════════════════════════

[HarmonyPatch]
public static class CardGenerationOriginPatch
{
    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterCardGeneratedForCombat))]
    [HarmonyPostfix]
    public static void AfterCardGenerated(CardModel card, bool addedByPlayer)
    {
        Safe.Run(() =>
        {
            if (!addedByPlayer) return;

            // If there's an active card context (e.g., CHARGE!! generating cards)
            var activeCard = CombatTracker.Instance.ActiveCardId;
            if (activeCard != null)
            {
                CombatTracker.Instance.RecordCardOrigin(
                    card.GetHashCode(), activeCard, "card");
                return;
            }

            // If there's an active power source (e.g., SpectrumShift generating cards)
            var powerSourceId = CombatTracker.Instance.ActivePowerSourceId;
            if (powerSourceId != null)
            {
                // Resolve to the card that created the power
                var source = ContributionMap.Instance.GetPowerSource(powerSourceId);
                if (source != null)
                {
                    CombatTracker.Instance.RecordCardOrigin(
                        card.GetHashCode(), source.SourceId, source.SourceType);
                }
            }
        });
    }
}

// ═══════════════════════════════════════════════════════════
// Osty Summon HP tracking
// Records which card/relic summoned Osty HP for LIFO defense attribution.
// ═══════════════════════════════════════════════════════════

[HarmonyPatch]
public static class OstySummonPatch
{
    /// <summary>
    /// After OstyCmd.Summon completes, record the HP added to Osty's LIFO stack.
    /// We use a Prefix to capture the source and amount, and Postfix to record.
    /// </summary>
    [HarmonyPatch(typeof(OstyCmd), nameof(OstyCmd.Summon))]
    [HarmonyPrefix]
    public static void BeforeSummon(Player summoner, decimal amount, AbstractModel? source,
        out (string sourceId, string sourceType, int amount) __state)
    {
        __state = ("", "", 0);
        try
        {
            int summonAmount = (int)amount;
            if (summonAmount <= 0) return;

            string sourceId;
            string sourceType;

            if (source is CardModel card)
            {
                sourceId = card.Id.Entry ?? "unknown";
                sourceType = "card";
            }
            else if (source is RelicModel relic)
            {
                sourceId = relic.Id.Entry ?? "unknown";
                sourceType = "relic";
            }
            else
            {
                // Fallback: check power source context (e.g., DevourLifePower, SicEmPower)
                var powerSrcId = CombatTracker.Instance.ActivePowerSourceId;
                if (powerSrcId != null)
                {
                    sourceId = powerSrcId;
                    sourceType = CombatTracker.Instance.ActivePowerSourceType ?? "card";
                }
                else
                {
                    sourceId = CombatTracker.Instance.ActiveCardId ?? "unknown";
                    sourceType = "card";
                }
            }

            __state = (sourceId, sourceType, summonAmount);
        }
        catch (Exception ex)
        {
            Safe.Warn($"[OstySummonPatch.Prefix] {ex.Message}");
        }
    }

    [HarmonyPatch(typeof(OstyCmd), nameof(OstyCmd.Summon))]
    [HarmonyPostfix]
    public static void AfterSummon((string sourceId, string sourceType, int amount) __state)
    {
        Safe.Run(() =>
        {
            if (__state.amount <= 0 || string.IsNullOrEmpty(__state.sourceId)) return;
            CombatTracker.Instance.OnOstySummoned(__state.sourceId, __state.sourceType, __state.amount);
        });
    }
}

// ═══════════════════════════════════════════════════════════
// Doom kill damage attribution
// Captures enemy HP before DoomKill, attributes as damage after.
// ═══════════════════════════════════════════════════════════

[HarmonyPatch]
public static class DoomKillPatch
{
    /// <summary>
    /// Before DoomPower.DoomKill executes, capture each creature's current HP.
    /// After kill, attribute total HP as damage to Doom source.
    /// </summary>
    [HarmonyPatch(typeof(DoomPower), nameof(DoomPower.DoomKill))]
    [HarmonyPrefix]
    public static void BeforeDoomKill(IReadOnlyList<Creature> creatures)
    {
        Safe.Run(() =>
        {
            foreach (var creature in creatures)
            {
                if (creature == null || !creature.IsAlive) continue;
                int hp = (int)creature.CurrentHp;
                if (hp > 0)
                {
                    CombatTracker.Instance.OnDoomTargetCapture(creature.GetHashCode(), hp);
                }
            }
        });
    }

    [HarmonyPatch(typeof(DoomPower), nameof(DoomPower.DoomKill))]
    [HarmonyPostfix]
    public static void AfterDoomKill()
    {
        Safe.Run(() =>
        {
            CombatTracker.Instance.OnDoomKillsCompleted();
        });
    }
}

// ═══════════════════════════════════════════════════════════
// Osty death tracking
// When Osty is killed (BoneShards sacrifice, Sacrifice card, combat death),
// clears LIFO HP stack and subtracts remaining HP from killer card's defense.
// ═══════════════════════════════════════════════════════════

[HarmonyPatch]
public static class OstyDeathPatch
{
    [HarmonyPatch(typeof(CreatureCmd), nameof(CreatureCmd.Kill),
        new Type[] { typeof(Creature), typeof(bool) })]
    [HarmonyPrefix]
    public static void BeforeKill(Creature creature)
    {
        Safe.Run(() =>
        {
            if (creature.Monster is Osty)
            {
                CombatTracker.Instance.OnOstyKilled();
            }
        });
    }
}

// NOTE: NecrobinderPowerHookContextPatch removed — consolidated into PowerHookContextPatcher.

// ═══════════════════════════════════════════════════════════
// Defect Orb contribution tracking
// Records which card channeled each orb, sets orb context during
// passive/evoke so damage/block/energy is attributed to the channeling card.
// Focus bonus is split out as ModifierDamage/ModifierBlock.
// ═══════════════════════════════════════════════════════════

[HarmonyPatch]
public static class OrbChanneledPatch
{
    /// <summary>
    /// After an orb is channeled, record the source card/relic that channeled it.
    /// Uses zero-parameter postfix to avoid Harmony parameter name mismatch issues.
    /// Reads the last orb from OrbQueue directly.
    /// </summary>
    [HarmonyPatch(typeof(CombatHistory), nameof(CombatHistory.OrbChanneled))]
    [HarmonyPostfix]
    public static void AfterOrbChanneled()
    {
        Safe.Run(() =>
        {
            // Power/relic source takes priority (matches ResolveSource order).
            // When Storm channels during another card's play, _activePowerSourceId = "STORM"
            // must take priority over _activeCardId = "DEFRAGMENT".
            string? sourceId = CombatTracker.Instance.ActivePowerSourceId;
            string sourceType = CombatTracker.Instance.ActivePowerSourceType ?? "card";

            if (sourceId == null)
            {
                sourceId = CombatTracker.Instance.ActiveRelicId;
                sourceType = "relic";
            }
            if (sourceId == null)
            {
                sourceId = CombatTracker.Instance.ActiveCardId;
                sourceType = "card";
            }
            if (sourceId == null) return;

            // Get the most recently channeled orb from the player's orb queue
            var combatState = CombatManager.Instance.DebugOnlyGetState();
            if (combatState == null) return;
            var player = MegaCrit.Sts2.Core.Context.LocalContext.GetMe(combatState);
            if (player == null) return;
            var orbs = player.PlayerCombatState.OrbQueue.Orbs;
            if (orbs.Count == 0) return;
            var orb = orbs[orbs.Count - 1]; // last channeled

            string orbType = orb switch
            {
                LightningOrb => "lightning",
                FrostOrb => "frost",
                DarkOrb => "dark",
                PlasmaOrb => "plasma",
                GlassOrb => "glass",
                _ => "unknown"
            };

            Godot.GD.Print($"[OrbTrack] Channel: hash={orb.GetHashCode()}, type={orbType}, source={sourceId}");
            ContributionMap.Instance.RecordOrbSource(
                orb.GetHashCode(), sourceId, sourceType, orbType);
        });
    }
}

[HarmonyPatch]
public static class OrbPassivePatch
{
    /// <summary>
    /// Hook.ModifyOrbPassiveTriggerCount postfix — tracks whether a modifying
    /// model (relic/power) raised the count. When it did, the *extra* trigger
    /// attributes to the modifying entity (e.g. GoldPlatedCables adds +1 to
    /// the front orb and that extra passive should go to the cables, not the
    /// orb's channeling card). Reset the per-orb counter each time the game
    /// recomputes so we start fresh per orb-passive batch.
    /// </summary>
    [HarmonyPatch(typeof(Hook), nameof(Hook.ModifyOrbPassiveTriggerCount))]
    [HarmonyPostfix]
    public static void AfterModifyOrbPassiveTriggerCount(int __result, OrbModel orb, int triggerCount, List<AbstractModel> modifyingModels)
    {
        Safe.Run(() =>
        {
            if (orb == null) return;
            int hash = orb.GetHashCode();
            ContributionMap.Instance.ResetOrbPassiveTriggerCount(hash);
            if (__result > triggerCount && modifyingModels != null && modifyingModels.Count > 0)
            {
                var last = modifyingModels[modifyingModels.Count - 1];
                var id = last.Id.Entry;
                if (!string.IsNullOrEmpty(id))
                {
                    string type = last is RelicModel ? "relic"
                                : last is PowerModel ? "power"
                                : "relic";
                    ContributionMap.Instance.SetOrbExtraTriggerSource(hash, id, type);
                }
            }
            else
            {
                ContributionMap.Instance.ClearOrbExtraTriggerSource(hash);
            }
        });
    }

    [HarmonyPatch(typeof(OrbCmd), nameof(OrbCmd.Passive))]
    [HarmonyPrefix]
    public static void BeforeOrbPassiveStatic(OrbModel orb)
    {
        Safe.Run(() => SetOrbContextForPassive(orb));
    }

    public static void PatchOrbTurnEndTriggers(Harmony harmony)
    {
        // AsyncLocal design: prefix sets _activeOrbContext (AsyncLocal), postfix
        // clears it in the caller's EC. Harmony postfix on an async method fires
        // when the method synchronously returns (i.e. after the state machine
        // suspends at its first await) — that's BEFORE the orb body's awaited
        // continuations resume, and BEFORE the caller's own `await` captures its
        // EC. So postfix clearing here does NOT zero out the orb body's
        // continuation view (it kept its pre-suspend captured EC with the set
        // value) but DOES clear the caller's EC so no leak to subsequent hooks.
        var prefix = new HarmonyMethod(AccessTools.Method(typeof(OrbPassivePatch), nameof(BeforeOrbTurnEnd)));
        var postfix = new HarmonyMethod(AccessTools.Method(typeof(OrbPassivePatch), nameof(AfterOrbTurnEnd)));
        var beforeTurnEndOrbs = new[] {
            typeof(LightningOrb), typeof(FrostOrb), typeof(DarkOrb), typeof(GlassOrb)
        };
        foreach (var t in beforeTurnEndOrbs)
        {
            try
            {
                var m = AccessTools.Method(t, "BeforeTurnEndOrbTrigger");
                if (m != null && m.DeclaringType == t)
                    harmony.Patch(m, prefix, postfix);
                else
                    Godot.GD.Print($"[OrbPatch] Skipped {t.Name}.BeforeTurnEndOrbTrigger (not overridden)");
            }
            catch (System.Exception ex)
            {
                Safe.Warn($"[OrbPatch] Failed {t.Name}.BeforeTurnEndOrbTrigger: {ex.Message}");
            }
        }

        try
        {
            var m = AccessTools.Method(typeof(PlasmaOrb), "AfterTurnStartOrbTrigger");
            if (m != null) harmony.Patch(m, prefix, postfix);
        }
        catch (System.Exception ex)
        {
            Safe.Warn($"[OrbPatch] Failed PlasmaOrb.AfterTurnStartOrbTrigger: {ex.Message}");
        }
    }

    public static void BeforeOrbTurnEnd(OrbModel __instance)
    {
        Safe.Run(() => SetOrbContextForPassive(__instance));
    }

    public static void AfterOrbTurnEnd()
    {
        Safe.Run(() => ContributionMap.Instance.ClearActiveOrbContext());
    }

    private static void SetOrbContextForPassive(OrbModel orb)
    {
        var source = ContributionMap.Instance.GetOrbSource(orb.GetHashCode());
        if (source == null) return;

        int hash = orb.GetHashCode();
        int triggerIdx = ContributionMap.Instance.IncrementOrbPassiveTriggerCount(hash);
        if (triggerIdx > 1)
        {
            var extra = ContributionMap.Instance.GetOrbExtraTriggerSource(hash);
            if (extra.HasValue)
            {
                ContributionMap.Instance.SetActiveOrbContext(
                    extra.Value.id, extra.Value.type, source.OrbType);
                SetOrbFocusContrib(orb);
                return;
            }
        }

        bool duringCardPlay = CombatTracker.Instance.ActiveCardId != null;
        if (duringCardPlay && ContributionMap.Instance.OrbFirstTriggerUsed)
        {
            ContributionMap.Instance.ClearActiveOrbContext();
            SetOrbFocusContrib(orb);
            return;
        }

        ContributionMap.Instance.SetActiveOrbContext(
            source.SourceId, source.SourceType, source.OrbType);
        if (duringCardPlay) ContributionMap.Instance.MarkOrbFirstTriggerUsed();
        SetOrbFocusContrib(orb);
    }

    [HarmonyPatch(typeof(OrbCmd), nameof(OrbCmd.Passive))]
    [HarmonyPostfix]
    public static void AfterOrbPassive()
    {
        Safe.Run(() => ContributionMap.Instance.ClearActiveOrbContext());
    }

    /// <summary>Shared Focus computation used by both passive and evoke patches.</summary>
    internal static void SetOrbFocusContrib(OrbModel orb)
    {
        if (orb is PlasmaOrb) return;
        var focusSource = ContributionMap.Instance.GetPowerSource("FOCUS_POWER")
                       ?? ContributionMap.Instance.GetPowerSource("FOCUS");
        if (focusSource == null) return;
        var player = orb.Owner;
        if (player?.Creature == null) return;
        var focusPower = player.Creature.GetPower<FocusPower>();
        if (focusPower != null && focusPower.Amount > 0)
        {
            ContributionMap.Instance.SetPendingOrbFocusContrib(
                focusSource.SourceId, focusSource.SourceType,
                (int)focusPower.Amount);
        }
    }
}

[HarmonyPatch]
public static class OrbEvokePatch
{
    /// <summary>
    /// Before orb evoke, set orb context and compute Focus split.
    /// We patch EvokeNext and EvokeLast since the private Evoke method isn't directly patchable.
    /// The orb to be evoked is the front (EvokeNext) or last (EvokeLast) of the queue.
    /// </summary>
    // EvokeNext/EvokeLast are static async methods — Harmony patches unreliable.
    // Instead, patch each orb type's Evoke instance method directly via PatchOrbEvokeMethods.

    public static void PatchOrbEvokeMethods(Harmony harmony)
    {
        // Same AsyncLocal design as PatchOrbTurnEndTriggers: prefix sets, postfix
        // clears the caller-flow view. Body's captured EC at the first internal
        // await keeps the set value, so evoke-originated damage/block still
        // attribute correctly; subsequent hooks in the caller's flow see null.
        var prefix = new HarmonyMethod(AccessTools.Method(typeof(OrbEvokePatch), nameof(BeforeOrbEvoke)));
        var postfix = new HarmonyMethod(AccessTools.Method(typeof(OrbEvokePatch), nameof(AfterOrbEvoke)));
        var orbTypes = new System.Type[] {
            typeof(LightningOrb), typeof(FrostOrb), typeof(DarkOrb),
            typeof(PlasmaOrb), typeof(GlassOrb)
        };
        foreach (var t in orbTypes)
        {
            try
            {
                var methods = t.GetMethods(System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.DeclaredOnly);
                var m = System.Array.Find(methods, mi => mi.Name == "Evoke");
                if (m != null)
                {
                    harmony.Patch(m, prefix, postfix);
                    Godot.GD.Print($"[OrbEvokePatch] Patched {t.Name}.Evoke");
                }
                else
                {
                    Godot.GD.Print($"[OrbEvokePatch] {t.Name}.Evoke not found (declared methods: {string.Join(", ", methods.Select(mi => mi.Name))})");
                }
            }
            catch (System.Exception ex)
            {
                Safe.Warn($"[OrbEvokePatch] Failed {t.Name}.Evoke: {ex.Message}");
            }
        }
    }

    public static void BeforeOrbEvoke(OrbModel __instance)
    {
        Safe.Run(() => SetOrbEvokeContext(__instance));
    }

    public static void AfterOrbEvoke()
    {
        Safe.Run(() => ContributionMap.Instance.ClearActiveOrbContext());
    }

    private static void SetOrbEvokeContext(OrbModel orb)
    {
        var source = ContributionMap.Instance.GetOrbSource(orb.GetHashCode());
        if (source == null) return;

        bool duringCardPlay = CombatTracker.Instance.ActiveCardId != null;
        if (duringCardPlay && ContributionMap.Instance.OrbFirstTriggerUsed)
        {
            ContributionMap.Instance.ClearActiveOrbContext();
            OrbPassivePatch.SetOrbFocusContrib(orb);
            return;
        }

        ContributionMap.Instance.SetActiveOrbContext(
            source.SourceId, source.SourceType, source.OrbType);

        if (duringCardPlay)
            ContributionMap.Instance.MarkOrbFirstTriggerUsed();

        OrbPassivePatch.SetOrbFocusContrib(orb);
    }
}

// NOTE: All PowerHookContext patches (Ironclad, Silent, Defect, Necrobinder,
// Regent, Shared) are applied via PowerHookContextPatcher.PatchAll()
// in CommunityStatsMod.Initialize(), NOT via [HarmonyPatch] attributes.

