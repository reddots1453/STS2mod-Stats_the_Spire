using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Ascension;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;

namespace CommunityStats.Util;

/// <summary>
/// Pre-baked monster intent state-machine metadata.
///
/// Background (PRD §3.10 round 6): the previous hover panel reached into
/// `creature.Monster.MoveStateMachine` at hover time and frequently saw a
/// null backing field — a Godot signal-callback timing race nobody could
/// reproduce reliably. Instead of fighting the timing, we walk every
/// monster prototype at **mod init** and snapshot its state machine into a
/// flat, immutable description that the hover panel can render without
/// touching any live combat state.
///
/// At init time we:
///   1. Iterate `ModelDb.Monsters` (~121 entries).
///   2. Mutable-clone each monster (the canonical instances are read-only).
///   3. Call `SetUpForCombat()` which assigns the `MoveStateMachine` field.
///   4. Walk every state, decode `MoveState` / `RandomBranchState` /
///      `ConditionalBranchState`, and copy the relevant fields into a POCO.
///   5. Store the result keyed by the monster id.
///
/// Init failures are tolerated per monster — a missing entry simply means
/// the hover panel falls back to "no metadata" instead of crashing.
/// </summary>
public static class MonsterIntentMetadata
{
    public enum StateKind { Move, RandomBranch, ConditionalBranch, Other }

    public sealed class IntentInfo
    {
        public string IntentTypeName { get; init; } = "";
        public IntentType IntentType { get; init; }
        public int? Damage { get; init; }
        public int? Block { get; init; }
        public int Repeats { get; init; } = 1;
        /// <summary>
        /// Round 9 round 4: live AbstractIntent instance (kept across the
        /// metadata cache lifetime) so the icon cache can read SpritePath
        /// via reflection — many intent classes have non-parameterless
        /// constructors so we can't cheaply re-instantiate them later.
        /// Plain managed object, no Godot binding, safe to cache.
        /// </summary>
        public AbstractIntent? IntentInstance { get; init; }
    }

    public sealed class BranchInfo
    {
        // Round 9 round 31: TargetStateId is mutable so monster-specific
        // override passes (e.g. Queen's SetMoveImmediate(Enrage)) can rewrite
        // a branch target without rebuilding the whole BranchInfo.
        public string TargetStateId { get; set; } = "";
        public float Weight { get; init; } = 1f;
        public int MaxTimes { get; init; }

        // Round 9 round 17: structural repetition rules captured from
        // RandomBranchState.StateWeight. Used to render annotations like
        // "≤1" / "≤N" / "×1" / "CD:N" next to the probability label, in the
        // STS1 mod style. The lambda's bare weight remains the displayed
        // percentage; these annotations describe the structural constraint.
        public MegaCrit.Sts2.Core.MonsterMoves.MoveRepeatType RepeatType { get; init; }
            = MegaCrit.Sts2.Core.MonsterMoves.MoveRepeatType.CanRepeatForever;
        public int Cooldown { get; init; }
    }

    public sealed class StateInfo
    {
        public string Id { get; init; } = "";
        public StateKind Kind { get; init; }
        public List<IntentInfo> Intents { get; init; } = new();
        // Round 9 round 31: FollowUpStateId is mutable so the CannotRepeat
        // unfolding post-pass can rewrite it without rebuilding the instance.
        public string? FollowUpStateId { get; set; }
        public List<BranchInfo> Branches { get; init; } = new();
    }

    public sealed class MonsterEntry
    {
        public string MonsterId { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public string? InitialStateId { get; init; }
        public List<StateInfo> States { get; init; } = new();
    }

    // Round 9 round 9: cache key is (monsterId, isDeadly) so we can hold both
    // ascension tiers simultaneously. `isDeadly` mirrors AscensionLevel.DeadlyEnemies
    // (A9+) which is the only ascension that changes intent damage / move sets.
    // A8 only adds ToughEnemies (HP only) so it shares the Standard tier per spec §3.10.7.
    private static readonly Dictionary<(string id, bool deadly), MonsterEntry?> _cache = new();
    public static int Count => _cache.Count;

    /// <summary>
    /// Round 9 round 9: forced ascension flag. While non-null, the Harmony prefix
    /// `AscensionForcePatch` returns this value from `RunManager.HasAscension` for
    /// the `DeadlyEnemies` and `ToughEnemies` levels (other levels pass through to
    /// the real implementation). Used by `Initialize()` to bake both tiers in one
    /// pass without needing an actual run in progress.
    /// </summary>
    internal static bool? ForcedAscensionDeadly;

    /// <summary>
    /// Returns true if the current run has Ascension 9+ (DeadlyEnemies). At
    /// non-run contexts (mod init / main menu) returns false. Wrapped in try
    /// because some code paths can be called before RunManager.Instance exists.
    /// </summary>
    private static bool IsCurrentRunDeadly()
    {
        try { return RunManager.Instance?.HasAscension(AscensionLevel.DeadlyEnemies) ?? false; }
        catch { return false; }
    }

    /// <summary>
    /// Look up a monster's pre-baked state-machine snapshot for the CURRENT run's
    /// ascension tier. Falls back to live in-combat MonsterModel.MoveStateMachine
    /// (which is always populated and always matches current ascension) when no
    /// cache entry exists.
    /// </summary>
    public static MonsterEntry? Get(string monsterId) =>
        Get(monsterId, liveMonster: null);

    public static MonsterEntry? Get(string monsterId, MonsterModel? liveMonster)
    {
        if (string.IsNullOrEmpty(monsterId)) return null;

        bool deadly = IsCurrentRunDeadly();
        var key = (monsterId, deadly);

        // Round 9 round 13: ALWAYS prefer live monster when available, even on
        // cache hits. Many monsters (TwoTailedRat, Hexaghost-style) have
        // RandomBranchState weight lambdas that capture `this` and read
        // dynamic counters like `_turnsUntilSummonable` / `_callForBackupCount`
        // / encounter slots. At eager-bake time those values are uninitialized
        // (default ints / null encounter), so the cached weights bear no
        // relation to actual combat. The live in-combat MonsterModel has those
        // counters at their CURRENT values, and the lambdas evaluate against
        // them — giving the same numbers the player sees in real moves.
        //
        // We DO NOT cache the live result: weights change every turn, so caching
        // would just re-create the staleness problem.
        if (liveMonster != null)
        {
            try
            {
                if (liveMonster.MoveStateMachine != null)
                {
                    var live = BuildEntryFromInstance(liveMonster);
                    if (live != null && live.States.Count > 0)
                        return live;
                }
            }
            catch (Exception ex)
            {
                Safe.Warn($"[IntentMeta] live build failed for {monsterId}: {ex.Message}");
            }
        }

        // Cache hit (used when no live monster, e.g. external callers / non-combat).
        if (_cache.TryGetValue(key, out var cached) && cached != null)
            return cached;

        // Last-resort: bake from canonical instance now.
        var entry = TryBakeFromCanonical(monsterId);
        if (entry != null)
        {
            _cache[key] = entry;
            return entry;
        }

        if (!_warnedMissing.Contains(monsterId))
        {
            _warnedMissing.Add(monsterId);
            Safe.Warn($"[IntentMeta] no metadata could be built for {monsterId} (will retry on next hover)");
        }
        return null;
    }

    private static readonly HashSet<string> _warnedMissing = new();

    /// <summary>
    /// Round 9 round 5: eager pre-bake at mod init. Walks every monster in
    /// `ModelDb.Monsters`, attempts a canonical clone bake, stores positive
    /// results in the cache. Failures are NOT cached so the lazy hover path
    /// can retry them with a live monster (whose state machine will already
    /// be populated by SetUpForCombat at combat start).
    ///
    /// Also reports a summary log so we can see how many bakes succeeded /
    /// failed at init time, helping diagnose missing-metadata reports.
    /// </summary>
    private static bool _eagerBakeDone;

    /// <summary>
    /// Round 9 round 9: bake every monster TWICE — once at Standard tier
    /// (no DeadlyEnemies/ToughEnemies) and once at Deadly tier (DeadlyEnemies+
    /// ToughEnemies forced on). The forcing is done by `AscensionForcePatch`
    /// reading `ForcedAscensionDeadly` and short-circuiting `RunManager.HasAscension`.
    /// </summary>
    private static void BakeAllMonsters(List<MonsterModel> monsters, bool deadly,
        ref int success, ref int failure, ref int total)
    {
        ForcedAscensionDeadly = deadly;
        try
        {
            foreach (var canonical in monsters)
            {
                total++;
                if (canonical?.Id?.Entry == null) { failure++; continue; }
                var id = canonical.Id.Entry;
                var key = (id, deadly);
                if (_cache.TryGetValue(key, out var existing) && existing != null)
                {
                    success++;
                    continue;
                }
                MonsterEntry? entry = null;
                try { entry = Bake(canonical); }
                catch (Exception ex)
                {
                    Safe.Warn($"[IntentMeta] eager bake threw for {id} (deadly={deadly}): {ex.Message}");
                }
                if (entry != null)
                {
                    _cache[key] = entry;
                    success++;
                }
                else
                {
                    failure++;
                }
            }
        }
        finally
        {
            ForcedAscensionDeadly = null;
        }
    }

    /// <summary>
    /// Round 9 round 6 fix: the previous version called `ModelDb.Monsters` which
    /// chains through `Acts.SelectMany(act.AllMonsters)` → `Act&lt;Overgrowth&gt;()` →
    /// `_contentById[GetId&lt;Overgrowth&gt;()]`. At mod-init time `ModelDb.Init()` has
    /// NOT yet run, so that dictionary lookup throws
    /// `KeyNotFoundException 'ACT.OVERGROWTH'` and the entire enumeration aborts
    /// before any monster gets baked (observed 0/0 in godot.log).
    ///
    /// New approach: read `ModelDb._contentById` directly via Traverse and filter
    /// its values to `MonsterModel` subclasses. This bypasses the Acts indirection
    /// entirely. If the dict is still empty (too early), we no-op — the method is
    /// also wired into `CombatLifecyclePatch.AfterSetUpCombat` so it re-runs at
    /// first combat start when ModelDb is guaranteed populated. Idempotent via
    /// the `_eagerBakeDone` flag.
    /// </summary>
    public static void Initialize()
    {
        if (_eagerBakeDone) return;

        int success = 0, failure = 0, total = 0;
        List<MonsterModel> monsters = new();
        try
        {
            var dict = Traverse.Create(typeof(ModelDb))
                .Field("_contentById")
                .GetValue<System.Collections.IDictionary>();
            if (dict == null)
            {
                Safe.Warn("[IntentMeta] eager bake skipped: ModelDb._contentById unreachable");
                return;
            }
            if (dict.Count == 0)
            {
                // ModelDb.Init hasn't run yet — defer to combat-start retry.
                Safe.Info("[IntentMeta] eager bake deferred: ModelDb not yet initialized");
                return;
            }
            foreach (var v in dict.Values)
            {
                if (v is MonsterModel m) monsters.Add(m);
            }
        }
        catch (Exception ex)
        {
            Safe.Warn($"[IntentMeta] eager bake enumeration failed: {ex.Message}");
            return;
        }

        // Round 9 round 9: double-bake. Standard tier first, then Deadly.
        int sStd = 0, fStd = 0, tStd = 0;
        BakeAllMonsters(monsters, deadly: false, ref sStd, ref fStd, ref tStd);

        int sDly = 0, fDly = 0, tDly = 0;
        BakeAllMonsters(monsters, deadly: true, ref sDly, ref fDly, ref tDly);

        success = sStd + sDly;
        failure = fStd + fDly;
        total = tStd + tDly;

        _eagerBakeDone = success > 0;
        Safe.Info($"[IntentMeta] eager bake complete: standard={sStd}/{tStd}, deadly={sDly}/{tDly}, failures={failure}");

        // Round 9 round 10: report monsters whose decode produced 0 branches in
        // any RandomBranch / ConditionalBranch state — these will render as
        // empty cyan boxes in the panel and indicate a decode regression.
        try
        {
            int emptyBranchMonsters = 0;
            foreach (var kv in _cache)
            {
                var ent = kv.Value;
                if (ent == null) continue;
                bool hasEmptyBranch = false;
                foreach (var s in ent.States)
                {
                    if ((s.Kind == StateKind.RandomBranch || s.Kind == StateKind.ConditionalBranch)
                        && s.Branches.Count == 0)
                    {
                        hasEmptyBranch = true;
                        break;
                    }
                }
                if (hasEmptyBranch) emptyBranchMonsters++;
            }
            if (emptyBranchMonsters > 0)
                Safe.Warn($"[IntentMeta] {emptyBranchMonsters} monsters have at least one branch state with 0 branches (will render as empty boxes)");
        }
        catch { }
    }

    private static MonsterEntry? TryBakeFromCanonical(string monsterId)
    {
        try
        {
            MonsterModel? canonical = null;
            foreach (var m in ModelDb.Monsters)
            {
                if (m?.Id?.Entry == monsterId) { canonical = m; break; }
            }
            if (canonical == null) return null;
            return Bake(canonical);
        }
        catch (Exception ex)
        {
            Safe.Warn($"[IntentMeta] canonical bake failed for {monsterId}: {ex.Message}");
            return null;
        }
    }

    private static MonsterEntry? BuildEntryFromInstance(MonsterModel m)
    {
        var sm = m.MoveStateMachine;
        if (sm?.States == null || sm.States.Count == 0) return null;

        string? initialId = null;
        try
        {
            var init = Traverse.Create(sm).Field("_initialState").GetValue<MonsterState>();
            initialId = init?.Id;
        }
        catch { }

        // Round 9 round 15: pass the live Creature owner so RandomBranch decoding
        // can call the game's own GetStateWeight (which applies CannotRepeat,
        // UseOnlyOnce, cooldown filters in addition to the lambda).
        Creature? owner = null;
        try { owner = m.Creature; } catch { }

        var states = new List<StateInfo>();
        foreach (var kv in sm.States)
        {
            try { states.Add(BuildState(kv.Key, kv.Value, owner)); }
            catch (Exception ex) { Safe.Warn($"[IntentMeta] {m.Id.Entry}.{kv.Key}: {ex.Message}"); }
        }
        UnfoldCannotRepeatFollowUps(states);
        ApplyMonsterSpecificOverrides(states, m.Id.Entry);
        return new MonsterEntry
        {
            MonsterId = m.Id.Entry,
            DisplayName = TryName(m, m.Id.Entry),
            InitialStateId = initialId,
            States = states,
        };
    }

    private static MonsterEntry? Bake(MonsterModel canonical)
    {
        if (canonical == null) return null;
        var id = canonical.Id?.Entry;
        if (string.IsNullOrEmpty(id)) return null;

        // Mutable clone — canonical instances are immutable so SetUpForCombat
        // would throw `AssertMutable`. We create a fresh mutable copy purely
        // to populate its MoveStateMachine field.
        MonsterModel mut;
        try
        {
            mut = canonical.ToMutable();
        }
        catch (Exception ex)
        {
            Safe.Warn($"[IntentMeta] {id}: ToMutable failed: {ex.Message}");
            return null;
        }

        try
        {
            mut.SetUpForCombat();
        }
        catch (Exception ex)
        {
            Safe.Warn($"[IntentMeta] {id}: SetUpForCombat failed: {ex.Message}");
            return null;
        }

        var sm = mut.MoveStateMachine;
        if (sm == null || sm.States == null || sm.States.Count == 0)
            return null;

        // Resolve initial state via reflection (private field _initialState).
        string? initialId = null;
        try
        {
            var init = Traverse.Create(sm).Field("_initialState").GetValue<MonsterState>();
            initialId = init?.Id;
        }
        catch { }

        // Bake from canonical clone — no live Creature owner, so RandomBranch
        // decoding falls back to the bare lambda (no CannotRepeat / UseOnlyOnce
        // filtering). The cache is only used outside combat anyway; in-combat
        // hovers go through BuildEntryFromInstance which has a live owner.
        var states = new List<StateInfo>();
        foreach (var kv in sm.States)
        {
            try { states.Add(BuildState(kv.Key, kv.Value, owner: null)); }
            catch (Exception ex) { Safe.Warn($"[IntentMeta] {id}.{kv.Key}: {ex.Message}"); }
        }
        UnfoldCannotRepeatFollowUps(states);
        ApplyMonsterSpecificOverrides(states, id!);

        return new MonsterEntry
        {
            MonsterId = id!,
            DisplayName = TryName(canonical, id!),
            InitialStateId = initialId,
            States = states,
        };
    }

    private static string TryName(MonsterModel m, string fallback)
    {
        try { return m.Title?.GetFormattedText() ?? fallback; }
        catch { return fallback; }
    }

    /// <summary>
    /// Round 9 round 31: CannotRepeat unfolding pass. For each MoveState `X`
    /// whose FollowUp is a RandomBranch `R`, if `R` contains `X` as a branch
    /// with MoveRepeatType.CannotRepeat, and removing `X` leaves exactly one
    /// candidate branch, rewrite `X.FollowUpStateId` to that unique remaining
    /// branch's target. Visualization then shows the effective next state,
    /// skipping the redundant RandomBranch pick that's structurally forced.
    /// Canonical case: Exoskeleton SKITTER → RAND(SKITTER cannotRepeat,
    /// MANDIBLE cannotRepeat) ⇒ SKITTER → MANDIBLE.
    /// </summary>
    private static void UnfoldCannotRepeatFollowUps(List<StateInfo> states)
    {
        var byId = new Dictionary<string, StateInfo>();
        foreach (var s in states) byId[s.Id] = s;

        foreach (var state in states)
        {
            if (state.Kind != StateKind.Move) continue;
            var fu = state.FollowUpStateId;
            if (string.IsNullOrEmpty(fu)) continue;
            if (!byId.TryGetValue(fu!, out var target)) continue;
            if (target.Kind != StateKind.RandomBranch) continue;

            int selfIdx = -1;
            for (int i = 0; i < target.Branches.Count; i++)
            {
                if (target.Branches[i].TargetStateId == state.Id) { selfIdx = i; break; }
            }
            if (selfIdx < 0) continue;
            if (target.Branches[selfIdx].RepeatType
                != MegaCrit.Sts2.Core.MonsterMoves.MoveRepeatType.CannotRepeat) continue;

            string? unique = null;
            bool multi = false;
            for (int i = 0; i < target.Branches.Count; i++)
            {
                if (i == selfIdx) continue;
                var t = target.Branches[i].TargetStateId;
                if (string.IsNullOrEmpty(t)) continue;
                if (unique == null) unique = t;
                else if (unique != t) { multi = true; break; }
            }
            if (multi || unique == null) continue;

            state.FollowUpStateId = unique;
        }
    }

    /// <summary>
    /// Round 9 round 31: monster-specific post-processing for runtime
    /// intent overrides that the pure state-machine read cannot see. Canonical
    /// case is Queen: `AmalgamDeathResponse` calls `SetMoveImmediate(Enrage)`
    /// when the Amalgam dies and Queen's queued move is BurnBrightForMe,
    /// jumping straight to Enrage without going through the OFF → EXECUTION
    /// → ENRAGE chain. Also Queen has two structurally identical Conditional
    /// Branch states (YOURE_MINE_NOW_BRANCH and BURN_BRIGHT_FOR_ME_BRANCH)
    /// which is visually redundant — we merge them into one box.
    /// </summary>
    private static void ApplyMonsterSpecificOverrides(List<StateInfo> states, string monsterId)
    {
        if (monsterId == "WRIGGLER")
        {
            // Rewrite SPAWNED.FollowUp from INIT_MOVE to BITE so INIT_MOVE
            // becomes dead code and gets hidden, leaving SPAWNED → BITE
            // direct + BITE↔WRIGGLE cycle. The "1/3号位" / "2/4号位" branch
            // semantics are surfaced via MonsterInitialVariants labels on
            // BITE and WRIGGLE.
            StateInfo? spawned = null, bite = null;
            foreach (var s in states)
            {
                if (s.Id == "SPAWNED_MOVE") spawned = s;
                else if (s.Id == "NASTY_BITE_MOVE") bite = s;
            }
            if (spawned != null && bite != null)
                spawned.FollowUpStateId = bite.Id;
        }

        if (monsterId == "QUEEN")
        {
            StateInfo? box1 = null;     // YOURE_MINE_NOW_BRANCH (canonical)
            StateInfo? box2 = null;     // BURN_BRIGHT_FOR_ME_BRANCH (merged into box1)
            StateInfo? burnBright = null;
            foreach (var s in states)
            {
                if (s.Id == "YOURE_MINE_NOW_BRANCH") box1 = s;
                else if (s.Id == "BURN_BRIGHT_FOR_ME_BRANCH") box2 = s;
                else if (s.Id == "BURN_BRIGHT_FOR_ME_MOVE") burnBright = s;
            }
            if (box1 != null && box2 != null && burnBright != null)
            {
                // Redirect BURN_BRIGHT's followup from Box2 to Box1 so the two
                // structurally identical conditional boxes merge into one.
                if (burnBright.FollowUpStateId == box2.Id)
                    burnBright.FollowUpStateId = box1.Id;

                // Rewrite Box1's "HasAmalgamDied" branch target from
                // OFF_WITH_YOUR_HEAD to ENRAGE, matching the runtime
                // SetMoveImmediate(Enrage) override.
                foreach (var b in box1.Branches)
                {
                    if (b.TargetStateId == "OFF_WITH_YOUR_HEAD_MOVE")
                        b.TargetStateId = "ENRAGE_MOVE";
                }
                // Box2 is now dead code (no incoming references). The panel's
                // dead-cell pass will hide it automatically.
            }
        }
    }

    private static StateInfo BuildState(string id, MonsterState state, Creature? owner)
    {
        switch (state)
        {
            case MoveState move:
                return new StateInfo
                {
                    Id = id,
                    Kind = StateKind.Move,
                    Intents = move.Intents?.Select(BuildIntent).ToList() ?? new(),
                    FollowUpStateId = move.FollowUpStateId ?? move.FollowUpState?.Id,
                };
            case RandomBranchState rnd:
                // Round 9 round 17: switch back to BARE LAMBDA weights per
                // user spec (option B — STS1 mod style). The runtime
                // GetStateWeight wrapper (round 9 round 15) and the
                // degenerate-picker fallback (round 9 round 16) are reverted —
                // the panel now shows the design-intent base distribution and
                // surfaces structural rules via separate annotations
                // (≤1 / ≤N / ×1 / CD:N) rendered next to the probability.
                // This is simpler, more readable, and avoids edge cases where
                // the picker's `num <= 0` short-circuit produces nonsense
                // (e.g. HauntedShip round 4 = 100%/0%/0%/0% because all
                // weights collapsed to 0 — accidental fallback, not design).
                var branches = new List<BranchInfo>();
                foreach (var sw in rnd.States)
                {
                    float w = 1f;
                    try { w = sw.GetWeight(); } catch { }
                    branches.Add(new BranchInfo
                    {
                        TargetStateId = sw.stateId,
                        Weight = Math.Max(0f, w),
                        MaxTimes = sw.maxTimes,
                        RepeatType = sw.repeatType,
                        Cooldown = sw.cooldown,
                    });
                }
                return new StateInfo { Id = id, Kind = StateKind.RandomBranch, Branches = branches };
            case ConditionalBranchState cond:
                // Round 9 round 10 fix: previous version used `Field("_branches")`
                // and `Field("stateId")` — both wrong. The actual decompiled
                // shape (see _decompiled/.../ConditionalBranchState.cs) is:
                //   private List<ConditionalBranch> States { get; } = new();
                //   private readonly struct ConditionalBranch {
                //       public readonly string id;
                //       private readonly Func<bool> _conditionalLambda;
                //   }
                // So the field is `States` (private auto-property) and the
                // inner field is `id`. Reading these via Traverse now succeeds.
                var condBranches = new List<BranchInfo>();
                try
                {
                    var rawList = Traverse.Create(cond).Property("States").GetValue<System.Collections.IEnumerable>();
                    if (rawList != null)
                    {
                        foreach (var entry in rawList)
                        {
                            var t = Traverse.Create(entry);
                            var stateId = t.Field("id").GetValue<string>();
                            if (!string.IsNullOrEmpty(stateId))
                                condBranches.Add(new BranchInfo
                                {
                                    TargetStateId = stateId,
                                    Weight = 1f, // conditional branches don't have explicit weights
                                });
                        }
                    }
                }
                catch (Exception ex)
                {
                    Safe.Warn($"[IntentMeta] ConditionalBranch decode failed for {id}: {ex.Message}");
                }
                return new StateInfo { Id = id, Kind = StateKind.ConditionalBranch, Branches = condBranches };
            default:
                return new StateInfo { Id = id, Kind = StateKind.Other };
        }
    }

    private static IntentInfo BuildIntent(AbstractIntent intent)
    {
        // Round 9 round 4: keep a reference to the live AbstractIntent
        // instance so IntentIconCache can read SpritePath via reflection
        // later (no need for a live combat owner — SpritePath is a class
        // constant on every concrete intent subclass).
        var info = new IntentInfo
        {
            IntentType = intent.IntentType,
            IntentTypeName = intent.GetType().Name,
            IntentInstance = intent,
        };

        try
        {
            if (intent is AttackIntent atk)
            {
                int repeats = atk.Repeats > 0 ? atk.Repeats : 1;
                int? baseDamage = null;
                var calc = atk.DamageCalc;
                if (calc != null)
                {
                    try { baseDamage = (int)calc(); } catch { }
                }
                return new IntentInfo
                {
                    IntentType = atk.IntentType,
                    IntentTypeName = atk.GetType().Name,
                    Damage = baseDamage,
                    Repeats = repeats,
                    IntentInstance = atk,
                };
            }
        }
        catch { }
        return info;
    }
}
