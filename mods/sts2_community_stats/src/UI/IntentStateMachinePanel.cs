using System.Collections.Generic;
using System.Linq;
using CommunityStats.Util;
using Godot;
using MegaCrit.Sts2.Core.Entities.Creatures;

namespace CommunityStats.UI;

/// <summary>
/// Hover panel showing a monster's intent state machine in the STS1
/// "Intents" mod style. **Round 9 round 11 rewrite** per PRD §3.10:
///
///   - Layered horizontal flow layout, depth-based columns.
///   - Branch-target Move states are HIDDEN from the standalone grid: they are
///     rendered ONLY inside their parent group box (no duplication). This is
///     the canonical STS1 "Intents" behaviour. Their followup arrows are
///     redirected to originate from the parent box.
///   - Group box renders each branch as a full-size mini cell whose content
///     mirrors the target Move state (multi-intent supported), with a
///     probability% / "条件" label overlaid in the top-left of every row.
///   - Multi-intent MoveState renders all intents side-by-side.
///   - Orthogonal (manhattan) arrows; never diagonal.
///
/// See `DesignDoc/UI_design_reference/intent_state_machine/` for the 30 STS1
/// reference screenshots driving every visual decision here.
/// </summary>
public static class IntentStateMachinePanel
{
    // ── Style constants (PRD §3.10.5) ───────────────────────
    private const int CellWidth      = 64;
    private const int CellHeight     = 80;
    private const int IconSize       = 48;
    // Round 9 round 12: intra-icon gap halved (6 → 3) for tighter multi-intent cells
    // Round 9 round 22: gap removed entirely (3 → 0) per user request
    private const int IntraIconGap   = 0;
    private const int CellPad        = 4;
    private const int LabelFont      = 13;
    private const int ProbFont       = 11;
    // Round 9 round 18/19: HGutter bumped progressively. The 3-segment manhattan
    // route needs visible space on both sides of the corner. Single-segment
    // forward arrows also benefit from extra breathing room between cells.
    // Final value 80: same-row direct line ≈ 62 px, manhattan exit+entry
    // segments each ~22-32 px. Panel gets wider but the arrows are clearly
    // separated from cells.
    private const int HGutter        = 80;
    private const int VGutter        = 24;
    // Round 9 round 21: PanelPad bumped 12 → 56 so back-edges going to the
    // leftmost cell (which start at d.X - HBend = leftmost.X - 48) and the
    // top channel (channelY = top.Y - 18) stay inside the panel boundary.
    private const int PanelPad       = 56;
    private const int GroupBoxPad    = 6;
    private const int GroupBoxBorder = 2;
    private const int GroupBoxRadius = 6;
    // Round 9 round 31: bumped from 2 → 16 so intra-box mini→mini arrows
    // (Fogmog SWIPE_RANDOM → HEADBUTT) have enough vertical space for the
    // arrow head + line.
    private const int RowSeparator   = 16;

    private static readonly Color CreamLabel  = new("#FFF6E2");
    private static readonly Color GoldLabel   = new("#EFC851");
    private static readonly Color GroupBorder = new("#29EBC0");
    private static readonly Color GroupBg     = new(0.10f, 0.14f, 0.22f, 0.55f);
    private static readonly Color GrayMissing = new(0.55f, 0.55f, 0.65f);
    private static readonly Color RedArrowColor = new("#FF4545");

    /// <summary>
    /// Round 9 round 25: monsters excluded from phase 1 distributed-label
    /// transform. Fabricator is temporarily blocklisted per user request
    /// because its nested Conditional→Random structure needs a more careful
    /// rework than the generic phase-1 rule handles.
    /// </summary>
    private static readonly HashSet<string> _phase1Blocklist = new() { "FABRICATOR" };

    /// <summary>
    /// Per-hover distributed initial label map (thread-local so nested
    /// BuildGroupBoxCell recursion shares it). Populated in Create(), consumed
    /// by BuildMoveCell / BuildGroupBoxCell for overlay rendering.
    /// </summary>
    [System.ThreadStatic]
    private static Dictionary<string, string>? _initialLabels;

    public static InfoModPanel? Create(Creature owner)
    {
        if (owner == null) return null;
        var monster = owner.Monster;
        if (monster == null) return null;

        string monsterId;
        try { monsterId = monster.Id?.Entry ?? "?"; }
        catch { monsterId = "?"; }

        var entry = MonsterIntentMetadata.Get(monsterId, monster);
        var displayName = TryName(monster, monsterId);
        var panel = InfoModPanel.Create(displayName);

        if (entry == null || entry.States.Count == 0)
        {
            panel.AddLabel("(no intent metadata)", GrayMissing);
            return panel;
        }

        // ── Build cells ──
        var cells = entry.States.Select(s => new Cell(s)).ToList();
        var cellById = cells.ToDictionary(c => c.State.Id, c => c);

        // Round 9 round 20/31: pick layout-start.
        // - Group box initial (Toadpole/Fabricator): use as-is, structural entry.
        // - Monster in MonsterInitialVariants table: actual initial varies
        //   per spawn (ScrollOfBiting CHOMP/CHEW/MORE_TEETH, Inklet variant,
        //   Wriggler StartStunned). Use first declared Move for stable layout.
        // - Otherwise: use the actual initial state read from the SM.
        string startId;
        var gameInitial = entry.InitialStateId;
        bool initialIsBox = !string.IsNullOrEmpty(gameInitial)
            && cellById.TryGetValue(gameInitial!, out var giCell)
            && (giCell.State.Kind == MonsterIntentMetadata.StateKind.RandomBranch
             || giCell.State.Kind == MonsterIntentMetadata.StateKind.ConditionalBranch);
        bool hasVariants = MonsterInitialVariants.Get(monsterId) != null;
        if (initialIsBox)
        {
            startId = gameInitial!;
        }
        else if (hasVariants)
        {
            var firstMove = entry.States.FirstOrDefault(s =>
                s.Kind == MonsterIntentMetadata.StateKind.Move);
            startId = firstMove?.Id ?? entry.States.First().Id;
        }
        else if (!string.IsNullOrEmpty(gameInitial) && cellById.ContainsKey(gameInitial!))
        {
            startId = gameInitial!;
        }
        else
        {
            var firstMove = entry.States.FirstOrDefault(s =>
                s.Kind == MonsterIntentMetadata.StateKind.Move);
            startId = firstMove?.Id ?? entry.States.First().Id;
        }

        // ── Round 9 round 25: distributed initial-condition labels ──
        // Phase 1: if the layout-start is a ConditionalBranchState (and the
        // monster is not blocklisted), its branches become "X号位初始"-style
        // labels distributed onto each target cell's top-left corner, and the
        // conditional box itself is hidden (collapsed into the target labels).
        // Example: Exoskeleton's INIT_MOVE disappears; SKITTER/MANDIBLE/ENRAGE
        // each get "N号位初始"; RAND (a box target) gets "4号位初始".
        // Phase 2: monsters whose initial is runtime-determined OUTSIDE the
        // SM (Inklet _middleInklet, Chomper _screamFirst, Wriggler StartStunned)
        // get hardcoded labels via MonsterInitialVariants.
        var initialLabels = new Dictionary<string, string>();
        bool phase1Active = false;

        // Phase 2: hardcoded variants. The table provides full labels
        // (already including "初始" or any custom suffix like "时"), so we
        // don't append anything here. Phase 1 (which uses MonsterConditionHints
        // building blocks) still appends "初始" since its hints are bare.
        var variants = MonsterInitialVariants.Get(monsterId);
        if (variants != null)
        {
            foreach (var (stateId, label) in variants)
                initialLabels[stateId] = label;
        }

        // Phase 1: layout-start is Conditional (and not Fabricator)
        if (cellById.TryGetValue(startId, out var startCell)
            && startCell.State.Kind == MonsterIntentMetadata.StateKind.ConditionalBranch
            && !_phase1Blocklist.Contains(monsterId))
        {
            phase1Active = true;
            // Group branches by target so multi-slot Wriggler-style conditions
            // merge into "1号位、3号位初始" rather than repeating.
            var byTarget = new Dictionary<string, List<string>>();
            for (int i = 0; i < startCell.State.Branches.Count; i++)
            {
                var b = startCell.State.Branches[i];
                if (string.IsNullOrEmpty(b.TargetStateId)) continue;
                var hint = MonsterConditionHints.Get(monsterId, i) ?? $"条件{i + 1}";
                if (!byTarget.TryGetValue(b.TargetStateId, out var list))
                {
                    list = new List<string>();
                    byTarget[b.TargetStateId] = list;
                }
                list.Add(hint);
            }
            foreach (var (targetId, labels) in byTarget)
            {
                initialLabels[targetId] = string.Join("、", labels) + "初始";
            }
        }

        // Phase 3 fallback: ensure the literal initial state always has a
        // label. Adds "初始" to startId if no phase-1/phase-2 entry already
        // covers it. Covers monsters with single deterministic Move initials
        // (花园幽灵鳗, Knowledge Demon, Queen PUPPET, TestSubject BITE, etc.)
        // AND coexists with variants entries (TerrorEel needs both CRASH "初始"
        // and STUN "HP<50%初始").
        if (cellById.TryGetValue(startId, out var initCell)
            && !initCell.IsHiddenInsideBox
            && !initialLabels.ContainsKey(startId))
        {
            initialLabels[startId] = "初始";
        }

        // `neverHide` — cells that must stay visible regardless of marking /
        // dead-cell / random-branch hide rules. Contains all labeled initial
        // targets plus the current startId.
        var neverHide = new HashSet<string>(initialLabels.Keys) { startId };

        // ── Mark cells that are referenced as branch targets ──
        // Round 9 round 24: distinguish RandomBranch vs ConditionalBranch hiding:
        //   - RandomBranch: hide ALL branch targets (rendered inside box only).
        //     STS1 mod style — clean compact box.
        //   - ConditionalBranch: hide ONLY branch targets that are themselves
        //     boxes (nested rendering). Move targets stay VISIBLE standalone
        //     so the user can see the chain (e.g. Toadpole's WHIRL→SPIKEN→
        //     SPIKE_SPIT, Knowledge Demon's CURSE→SLAP→OVERWHELMING→PONDER).
        //     The conditional box still shows mini-cells inside as a "decision
        //     menu" with conditions; the standalone cells form the chain.
        // Initial-state and first-parent-wins rules still apply.
        foreach (var c in cells)
        {
            if (c.State.Kind != MonsterIntentMetadata.StateKind.RandomBranch
             && c.State.Kind != MonsterIntentMetadata.StateKind.ConditionalBranch)
                continue;
            bool isConditional = c.State.Kind == MonsterIntentMetadata.StateKind.ConditionalBranch;
            foreach (var b in c.State.Branches)
            {
                if (string.IsNullOrEmpty(b.TargetStateId)) continue;
                if (!cellById.TryGetValue(b.TargetStateId, out var target)) continue;
                if (target.IsHiddenInsideBox) continue; // first parent wins
                if (neverHide.Contains(target.State.Id)) continue; // initial / variant
                // Conditional branches don't hide Move targets (they need to
                // appear standalone for chain visibility).
                if (isConditional && target.State.Kind == MonsterIntentMetadata.StateKind.Move)
                    continue;
                target.IsHiddenInsideBox = true;
                target.ParentBoxStateId = c.State.Id;
            }
        }

        // Round 9 round 21: hide blank Move cells (no intents). Door's
        // DEAD_MOVE is the canonical case — a placeholder used for revival
        // logic. With no intents to render and a self-loop on itself, it would
        // otherwise show up as a transparent square + dangling self-loop arrow.
        // ParentBoxStateId stays null → arrow routing skips edges to/from it.
        foreach (var c in cells)
        {
            if (c.State.Kind == MonsterIntentMetadata.StateKind.Move
                && c.State.Intents.Count == 0)
            {
                c.IsHiddenInsideBox = true;
            }
        }

        // Round 9 round 23/24: hide DEAD cells — states with no incoming
        // edges (no other state's followup or branch targets them) AND not
        // the initial state. Two canonical cases:
        //   - PhrogParasite's `RAND` is a RandomBranchState that's declared
        //     but never reached.
        //   - BygoneEffigy's `SLEEP_MOVE` is a Move state that's declared
        //     but never reached (only `INITIAL_SLEEP_MOVE` is in the chain;
        //     `SLEEP_MOVE` is dead code that produces a confusing duplicate
        //     "zZ" icon).
        var hasIncoming = new HashSet<string>();
        foreach (var c in cells)
        {
            if (!string.IsNullOrEmpty(c.State.FollowUpStateId))
                hasIncoming.Add(c.State.FollowUpStateId!);
            foreach (var b in c.State.Branches)
                if (!string.IsNullOrEmpty(b.TargetStateId))
                    hasIncoming.Add(b.TargetStateId);
        }
        foreach (var c in cells)
        {
            if (c.IsHiddenInsideBox) continue;
            if (neverHide.Contains(c.State.Id)) continue; // initial / variant
            if (hasIncoming.Contains(c.State.Id)) continue;
            c.IsHiddenInsideBox = true;
        }

        // Round 9 round 29: un-hide orphans whose parent got dead-hidden.
        // Canonical case: PhrogParasite's RAND is a dead RandomBranchState
        // (never reached). Marking hid LASH as a branch target of RAND with
        // ParentBoxStateId = RAND. Then the dead-cell pass hid RAND itself.
        // Now LASH is marked hidden but its "parent" is also hidden with no
        // rect — arrow routing can't find it → LASH becomes invisible and
        // the whole INFECT↔LASH cycle disappears. Fix: un-hide such orphans.
        foreach (var c in cells)
        {
            if (!c.IsHiddenInsideBox) continue;
            if (c.ParentBoxStateId == null) continue;
            if (!cellById.TryGetValue(c.ParentBoxStateId, out var parent)) continue;
            // Parent was hidden by the dead-cell rule iff it's hidden AND has
            // no ParentBoxStateId of its own (phase-1 hidden boxes set a
            // parent id to their new startId, so those are distinguishable).
            if (parent.IsHiddenInsideBox && parent.ParentBoxStateId == null)
            {
                c.IsHiddenInsideBox = false;
                c.ParentBoxStateId = null;
            }
        }

        // Round 9 round 31: hide Move cells that are ONLY reachable via a
        // ConditionalBranch (no non-branch FollowUp points to them). Such
        // cells are fully represented by their mini appearance inside the
        // parent box, so keeping a separate standalone duplicates the icon.
        // Canonical case: Queen post-override has BURN_BRIGHT as a branch
        // target with no non-branch incoming (BURN_BRIGHT.FollowUp points
        // back to the box itself). Hiding its standalone leaves only the
        // mini inside Box1 with a self-loop bracket. ENRAGE is NOT hidden
        // because EXECUTION.FollowUp = ENRAGE gives it a non-branch incoming.
        var nonBranchIncoming = new HashSet<string>();
        foreach (var c in cells)
        {
            if (!string.IsNullOrEmpty(c.State.FollowUpStateId))
                nonBranchIncoming.Add(c.State.FollowUpStateId!);
        }
        foreach (var c in cells)
        {
            if (c.IsHiddenInsideBox) continue;
            if (c.State.Kind != MonsterIntentMetadata.StateKind.Move) continue;
            if (neverHide.Contains(c.State.Id)) continue;
            if (nonBranchIncoming.Contains(c.State.Id)) continue;
            string? parentBoxId = null;
            foreach (var boxCell in cells)
            {
                if (boxCell.IsHiddenInsideBox) continue;
                if (boxCell.State.Kind != MonsterIntentMetadata.StateKind.ConditionalBranch)
                    continue;
                foreach (var b in boxCell.State.Branches)
                {
                    if (b.TargetStateId == c.State.Id)
                    {
                        parentBoxId = boxCell.State.Id;
                        break;
                    }
                }
                if (parentBoxId != null) break;
            }
            if (parentBoxId != null)
            {
                c.IsHiddenInsideBox = true;
                c.ParentBoxStateId = parentBoxId;
            }
        }

        // Round 9 round 25 — phase 1: hide the initial ConditionalBranch box
        // itself. Its branch conditions have been distributed into target
        // cells' top-left labels, so the box no longer needs to render. We
        // set ParentBoxStateId = one of the branch targets so arrows that
        // would land on the hidden box (e.g. SPAWNED.followup → INIT_MOVE
        // when Wriggler is StartStunned) still land on a visible cell.
        if (phase1Active && cellById.TryGetValue(startId, out var origInitial))
        {
            // Find the first initial target that isn't hidden, to use both
            // as the new layout-start and as the redirect target for arrows.
            string? newStartId = null;
            foreach (var b in origInitial.State.Branches)
            {
                if (string.IsNullOrEmpty(b.TargetStateId)) continue;
                if (!cellById.TryGetValue(b.TargetStateId, out var t)) continue;
                if (t.IsHiddenInsideBox) continue;
                newStartId = t.State.Id;
                break;
            }
            if (newStartId != null)
            {
                origInitial.IsHiddenInsideBox = true;
                origInitial.ParentBoxStateId = newStartId;
                startId = newStartId;
            }
        }

        // ── Compute sizes: Move cells first, group boxes second ──
        // (Group box size depends on its child Move cells' sizes.)
        foreach (var c in cells)
        {
            if (c.State.Kind == MonsterIntentMetadata.StateKind.Move)
                (c.Width, c.Height) = ComputeMoveSize(c.State);
        }
        // Round 9 round 19: iterate box sizing to a fixed point. A box's size
        // depends on its children's sizes. When a box's child is ITSELF a box
        // (e.g. Fabricator's ConditionalBranchState contains a RandomBranchState
        // as one branch), a single forward pass uses (0, 0) for the still-
        // unsized inner box and the outer box ends up too small. After enough
        // iterations all sizes converge.
        bool sizeChanged = true;
        int sizeSafety = 0;
        while (sizeChanged && sizeSafety++ < 8)
        {
            sizeChanged = false;
            foreach (var c in cells)
            {
                if (c.State.Kind != MonsterIntentMetadata.StateKind.RandomBranch
                 && c.State.Kind != MonsterIntentMetadata.StateKind.ConditionalBranch)
                    continue;
                var (newW, newH) = ComputeBoxSize(c.State, cellById);
                if (newW != c.Width || newH != c.Height)
                {
                    c.Width = newW;
                    c.Height = newH;
                    sizeChanged = true;
                }
            }
        }

        // Round 9 round 25: cells with a distributed-initial label need extra
        // top space to fit the "X号位初始" overlay without overlapping the
        // icon. Grow the cell height by ProbFont+4 — this matches the
        // top-offset BuildMoveCell/BuildGroupBoxCell use when rendering.
        const int InitialLabelSlot = ProbFont + 4;
        foreach (var c in cells)
        {
            if (initialLabels.ContainsKey(c.State.Id))
                c.Height += InitialLabelSlot;
        }

        // ── Compute depth (BFS from initial state) ──
        AssignDepths(cells, cellById, startId!);

        // Round 9 round 23: compact depth indices per row. After back-prop
        // shifts, hidden cells often occupy depth slots that have no visible
        // cells at all (e.g. Decimillipede's DEAD_MOVE at depth 0 with all
        // other visible cells at depth 1+). Re-index visible cells' depths
        // to be consecutive 0,1,2,... so the layout doesn't have empty
        // leading/trailing/intermediate columns.
        {
            var rowsSeen = new HashSet<int>();
            foreach (var c in cells) rowsSeen.Add(c.Row);
            foreach (var rowId in rowsSeen)
            {
                var visibleInRow = cells
                    .Where(c => c.Row == rowId && !c.IsHiddenInsideBox)
                    .Select(c => c.Depth)
                    .Distinct()
                    .OrderBy(d => d)
                    .ToList();
                if (visibleInRow.Count == 0) continue;
                var depthMap = new Dictionary<int, int>();
                for (int i = 0; i < visibleInRow.Count; i++)
                    depthMap[visibleInRow[i]] = i;
                foreach (var c in cells)
                {
                    if (c.Row == rowId && depthMap.TryGetValue(c.Depth, out var newD))
                        c.Depth = newD;
                }
            }
        }

        // ── MULTI-ROW LAYOUT (Round 9 round 21) ──
        // Group VISIBLE cells by Row, then within each Row by Depth (column).
        // Each row has its own per-column width array; rows stack vertically.
        // BFS order is used for within-column ordering (when multiple cells
        // share the same row+depth, they stack in BFS visit order).
        var visibleCells = cells.Where(c => !c.IsHiddenInsideBox).ToList();
        int rowCount = visibleCells.Count == 0 ? 1 : visibleCells.Max(c => c.Row) + 1;

        var bfsOrder = BfsOrder(cells, cellById, startId!);
        var bfsOrderIndex = new Dictionary<string, int>();
        for (int i = 0; i < bfsOrder.Count; i++)
            bfsOrderIndex[bfsOrder[i].State.Id] = i;

        int contentTop = PanelPad;
        int contentBottom = contentTop;
        int maxRowRight = 0;

        int rowTopY = contentTop;
        for (int r = 0; r < rowCount; r++)
        {
            var rCells = visibleCells.Where(c => c.Row == r).ToList();
            if (rCells.Count == 0) continue;
            int rMaxDepth = rCells.Max(c => c.Depth);
            var rColW = new int[rMaxDepth + 1];
            foreach (var c in rCells)
                if (c.Width > rColW[c.Depth]) rColW[c.Depth] = c.Width;

            // Column X positions for this row
            var rColX = new int[rMaxDepth + 1];
            int cur = PanelPad;
            for (int d = 0; d <= rMaxDepth; d++)
            {
                rColX[d] = cur;
                cur += rColW[d] + HGutter;
            }
            int rowRight = cur - HGutter + PanelPad;
            if (rowRight > maxRowRight) maxRowRight = rowRight;

            // Group cells by depth, sort within column by BFS visit order
            var byDepth = new Dictionary<int, List<Cell>>();
            foreach (var c in rCells)
            {
                if (!byDepth.TryGetValue(c.Depth, out var list)) { list = new List<Cell>(); byDepth[c.Depth] = list; }
                list.Add(c);
            }
            // Round 9 round 28: vertically CENTER each column's stack inside
            // the row's max column height, so single-cell columns with
            // different heights end up with their mid-Ys aligned. Without
            // this, Toadpole's WHIRL/SPIKEN (height bumped by the "1号位初始"
            // label) top-aligned with SPIKE_SPIT (no label, normal height)
            // produce different mid-Ys — and forward arrows between them
            // degenerate into 3-segment manhattan bends instead of clean
            // horizontal lines. Pre-pass: compute rowMaxColHeight first.
            int rowMaxColHeight = 0;
            foreach (var (depth, list) in byDepth)
            {
                list.Sort((a, b) =>
                {
                    int ai = bfsOrderIndex.TryGetValue(a.State.Id, out var av) ? av : int.MaxValue;
                    int bi = bfsOrderIndex.TryGetValue(b.State.Id, out var bv) ? bv : int.MaxValue;
                    return ai.CompareTo(bi);
                });
                int colH = list.Sum(c => c.Height) + (list.Count - 1) * VGutter;
                if (colH > rowMaxColHeight) rowMaxColHeight = colH;
            }
            // Second pass: place each column's stack centered in rowMaxColHeight.
            foreach (var (depth, list) in byDepth)
            {
                int colH = list.Sum(c => c.Height) + (list.Count - 1) * VGutter;
                int y = rowTopY + (rowMaxColHeight - colH) / 2;
                foreach (var c in list)
                {
                    c.X = rColX[depth];
                    c.Y = y;
                    y += c.Height + VGutter;
                }
            }

            int rowBot = rowTopY + rowMaxColHeight;
            if (rowBot > contentBottom) contentBottom = rowBot;
            // Round 9 round 29: more vertical gap between rows so the red
            // phase-transition arrow between multi-phase boss rows doesn't
            // get occluded by the upper row's phase box border.
            rowTopY = rowBot + VGutter * 4;
        }

        // Round 9 round 13/21: pad surface so back-edge brackets and self-loops
        // fully fit inside the panel boundary.
        int surfaceW = System.Math.Max(maxRowRight, 120) + 64;
        int surfaceH = contentBottom + PanelPad + 32;

        // ── Build the surface Control ──
        var surface = new Control { CustomMinimumSize = new Vector2(surfaceW, surfaceH) };
        surface.MouseFilter = Control.MouseFilterEnum.Ignore;
        panel.AddCustom(surface);

        // ── Round 9 round 28: multi-phase box wrapping ──
        // For monsters whose state machine has multiple disconnected subgraphs
        // (rowCount > 1 after multi-row layout), each row represents a "phase"
        // of a multi-phase boss. Wrap each row's cells in a cyan-border
        // container box (drawn UNDER the cells) and draw a red transition
        // arrow between consecutive phases with no text.
        // CeremonialBeast is the canonical case: phase 1 (STAMP→PLOW) and
        // phase 2 (STUN→BEAST_CRY→STOMP→CRUSH).
        bool multiPhase = rowCount > 1;
        var phaseBoxRects = new List<Rect2>();
        if (multiPhase)
        {
            for (int r = 0; r < rowCount; r++)
            {
                var rCells2 = visibleCells.Where(c => c.Row == r).ToList();
                if (rCells2.Count == 0) continue;
                int minX = rCells2.Min(c => c.X) - 8;
                int maxX = rCells2.Max(c => c.X + c.Width) + 8;
                int minY = rCells2.Min(c => c.Y) - 8;
                int maxY = rCells2.Max(c => c.Y + c.Height) + 8;
                phaseBoxRects.Add(new Rect2(minX, minY, maxX - minX, maxY - minY));

                var phaseBox = new Panel { Name = $"PhaseBox_{r}" };
                var style = new StyleBoxFlat
                {
                    BgColor = GroupBg,
                    BorderColor = GroupBorder,
                    BorderWidthLeft = GroupBoxBorder,
                    BorderWidthRight = GroupBoxBorder,
                    BorderWidthTop = GroupBoxBorder,
                    BorderWidthBottom = GroupBoxBorder,
                    CornerRadiusTopLeft = GroupBoxRadius,
                    CornerRadiusTopRight = GroupBoxRadius,
                    CornerRadiusBottomLeft = GroupBoxRadius,
                    CornerRadiusBottomRight = GroupBoxRadius,
                };
                phaseBox.AddThemeStyleboxOverride("panel", style);
                phaseBox.MouseFilter = Control.MouseFilterEnum.Ignore;
                phaseBox.Position = new Vector2(minX, minY);
                phaseBox.Size = new Vector2(maxX - minX, maxY - minY);
                phaseBox.ZIndex = -1; // draw UNDER the cells
                surface.AddChild(phaseBox);
            }
        }

        // ── Place visible cells; build cellRects for arrow routing ──
        // Round 9 round 25: stash initialLabels in a ThreadStatic so recursive
        // BuildGroupBoxCell / BuildMoveCell can consume it without signature
        // changes. Cleared in the finally block to avoid cross-panel leakage.
        _initialLabels = initialLabels;
        var cellRects = new Dictionary<string, Rect2>();
        try
        {
            foreach (var c in cells)
            {
                if (c.IsHiddenInsideBox) continue;
                var node = BuildCellNode(c, cellById, monsterId);
                node.Position = new Vector2(c.X, c.Y);
                node.Size = new Vector2(c.Width, c.Height);
                surface.AddChild(node);
                cellRects[c.State.Id] = new Rect2(c.X, c.Y, c.Width, c.Height);
            }
        }
        finally
        {
            _initialLabels = null;
        }

        // ── Round 9 round 28: red phase transition arrows ──
        // Draw a short red arrow from phase N's bottom-center to phase N+1's
        // top-center. These are implicit state-machine-external transitions
        // (e.g. HP threshold or game logic), so no text label.
        if (multiPhase && phaseBoxRects.Count >= 2)
        {
            for (int r = 0; r + 1 < phaseBoxRects.Count; r++)
            {
                var from = phaseBoxRects[r];
                var to = phaseBoxRects[r + 1];
                float fromX = from.Position.X + from.Size.X / 2f;
                float fromY = from.Position.Y + from.Size.Y;
                float toX = to.Position.X + to.Size.X / 2f;
                float toY = to.Position.Y;
                // Simple arrow: from phase N bottom-center down to phase N+1
                // top-center, straight vertical line.
                var arrowNode = new Control { Name = $"PhaseArrow_{r}" };
                arrowNode.MouseFilter = Control.MouseFilterEnum.Ignore;
                surface.AddChild(arrowNode);
                arrowNode.ZIndex = 20;
                // Use a Line2D + Polygon2D directly on the surface (not via ArrowOverlay)
                var line = new Line2D
                {
                    DefaultColor = RedArrowColor,
                    Width = 7.5f,
                    Antialiased = true,
                    JointMode = Line2D.LineJointMode.Sharp,
                    BeginCapMode = Line2D.LineCapMode.None,
                    EndCapMode = Line2D.LineCapMode.None,
                    ZIndex = 20,
                };
                line.AddPoint(new Vector2(fromX, fromY));
                // bend horizontally if from/to X differ
                if (!Mathf.IsEqualApprox(fromX, toX))
                {
                    float midY = (fromY + toY) / 2f;
                    line.AddPoint(new Vector2(fromX, midY));
                    line.AddPoint(new Vector2(toX, midY));
                }
                var pullBack = new Vector2(toX, toY - 12f);
                line.AddPoint(pullBack);
                surface.AddChild(line);
                // Triangle head
                var dir = new Vector2(0, 1);
                var perp = new Vector2(-dir.Y, dir.X);
                var head = new Polygon2D
                {
                    Color = RedArrowColor,
                    Polygon = new[]
                    {
                        new Vector2(toX, toY + 4f),
                        pullBack + perp * 9.9f,
                        pullBack - perp * 9.9f,
                    },
                    ZIndex = 20,
                };
                surface.AddChild(head);
            }
        }

        // ── Compute mini-cell rects (Round 9 round 26) ──
        // For each visible group box, record the absolute rect of each of its
        // branch mini-cells (as placed inside the box by BuildGroupBoxCell).
        // ArrowOverlay uses these so that hidden branch targets (CHEW inside
        // ScrollOfBiting's rand) get their OWN outgoing arrow from the mini-
        // cell position, instead of redirecting to the parent box as a single
        // ambiguous box-level self-loop. Each branch target can also have
        // MULTIPLE appearances across boxes (Queen BurnBright appears in two
        // conditionals; Wriggler NASTY appears twice in INIT_MOVE), so the
        // dict value is a LIST of rects.
        var miniCellRects = new Dictionary<string, List<Rect2>>();
        foreach (var c in cells)
        {
            if (c.IsHiddenInsideBox) continue;
            if (c.State.Kind != MonsterIntentMetadata.StateKind.RandomBranch
             && c.State.Kind != MonsterIntentMetadata.StateKind.ConditionalBranch)
                continue;
            int innerLeft = c.X + GroupBoxBorder + GroupBoxPad;
            int rowY = c.Y + GroupBoxBorder + GroupBoxPad;
            if (initialLabels.ContainsKey(c.State.Id))
                rowY += ProbFont + 4;
            for (int i = 0; i < c.State.Branches.Count; i++)
            {
                var b = c.State.Branches[i];
                if (string.IsNullOrEmpty(b.TargetStateId)) continue;
                int rowW = CellWidth;
                int rowH = CellHeight;
                if (cellById.TryGetValue(b.TargetStateId, out var t))
                {
                    rowW = t.Width;
                    rowH = t.Height;
                }
                var rect = new Rect2(innerLeft, rowY, rowW, rowH);
                if (!miniCellRects.TryGetValue(b.TargetStateId, out var list))
                {
                    list = new List<Rect2>();
                    miniCellRects[b.TargetStateId] = list;
                }
                list.Add(rect);
                rowY += rowH + RowSeparator;
            }
        }

        // ── Arrows ──
        var arrows = new ArrowOverlay();
        arrows.Position = Vector2.Zero;
        arrows.Size = new Vector2(surfaceW, surfaceH);
        arrows.CustomMinimumSize = new Vector2(surfaceW, surfaceH);
        arrows.MouseFilter = Control.MouseFilterEnum.Ignore;
        arrows.ZIndex = 10;
        surface.AddChild(arrows);
        arrows.Build(cells, cellById, cellRects, miniCellRects);

        return panel;
    }

    // ── Cell layout types ───────────────────────────────────

    public sealed class Cell
    {
        public MonsterIntentMetadata.StateInfo State;
        public int Depth;
        /// <summary>
        /// Round 9 round 21: row index for multi-row layout. The main reachable
        /// component lives in row 0; each disconnected subgraph (e.g. TestSubject's
        /// phase-2 / phase-3 chains, CeremonialBeast's STUN cycle) gets its own
        /// row stacked below row 0 instead of all collapsing into row 0 col 0.
        /// </summary>
        public int Row;
        public int X, Y;
        public int Width, Height;

        /// <summary>
        /// True if this cell is referenced as a branch target by a parent box
        /// state — in that case it's drawn INSIDE the box and excluded from the
        /// standalone grid layout.
        /// Also set for blank Move cells (0 intents — e.g. Door's DEAD_MOVE
        /// placeholder) so they don't take up a column slot. ParentBoxStateId
        /// stays null in that case → arrow routing skips edges to/from them.
        /// </summary>
        public bool IsHiddenInsideBox;

        /// <summary>
        /// State id of the parent group box that owns this cell, when hidden.
        /// </summary>
        public string? ParentBoxStateId;

        public Cell(MonsterIntentMetadata.StateInfo s)
        {
            State = s;
        }
    }

    private static (int w, int h) ComputeMoveSize(MonsterIntentMetadata.StateInfo s)
    {
        int icons = System.Math.Max(1, s.Intents.Count);
        int w = System.Math.Max(CellWidth, icons * IconSize + (icons - 1) * IntraIconGap + 2 * CellPad);
        return (w, CellHeight);
    }

    private static (int w, int h) ComputeBoxSize(
        MonsterIntentMetadata.StateInfo s,
        Dictionary<string, Cell> byId)
    {
        var branches = s.Branches;
        if (branches.Count == 0)
        {
            // Decode failure / empty: draw a small placeholder box.
            return (CellWidth + 2 * GroupBoxPad + 2 * GroupBoxBorder,
                    CellHeight + 2 * GroupBoxPad + 2 * GroupBoxBorder);
        }
        int innerW = 0;
        int innerH = 0;
        for (int i = 0; i < branches.Count; i++)
        {
            var b = branches[i];
            int rowW = CellWidth;
            int rowH = CellHeight;
            if (!string.IsNullOrEmpty(b.TargetStateId)
                && byId.TryGetValue(b.TargetStateId, out var t))
            {
                rowW = t.Width;
                rowH = t.Height;
            }
            if (rowW > innerW) innerW = rowW;
            innerH += rowH;
            if (i < branches.Count - 1) innerH += RowSeparator;
        }
        return (innerW + 2 * GroupBoxPad + 2 * GroupBoxBorder,
                innerH + 2 * GroupBoxPad + 2 * GroupBoxBorder);
    }

    // ── BFS / depth ─────────────────────────────────────────

    private static void AssignDepths(List<Cell> cells, Dictionary<string, Cell> byId, string startId)
    {
        // Round 9 round 31: SCC-based row assignment. Tarjan's algorithm
        // identifies strongly-connected components in the followup+branch
        // graph. Each multi-cell SCC ("phase cluster") gets its own row.
        // Singletons share a single "linear" row in topological order.
        // The initial state's SCC is always row 0.
        // Canonical case: TestSubject has 3 phase loops (BITE↔SKULL_BASH,
        // POUNCE↔MULTI_CLAW, LACERATE→BIG_POUNCE→BURNING_GROWL→LACERATE)
        // plus RESPAWN→REVIVE_BRANCH chain. Each loop becomes its own row.
        foreach (var c in cells) { c.Depth = 0; c.Row = -1; }

        var visibleCells = new List<Cell>();
        foreach (var c in cells)
            if (!c.IsHiddenInsideBox) visibleCells.Add(c);
        if (visibleCells.Count == 0) return;

        var sccs = ComputeSCCs(visibleCells, byId);
        if (sccs.Count == 0) return;

        // Round 9 round 31: only use SCC layout when there are ≥2 multi-cell
        // SCCs (i.e. multiple "phase clusters"). Monsters with a single
        // dominant cycle (Wriggler, Knowledge Demon, Queen, Inklet, etc.)
        // fall back to BFS layout — putting their lone SCC and any singletons
        // in the same row works better than a 2-row split.
        int multiSccCount = 0;
        foreach (var s in sccs) if (s.Count >= 2) multiSccCount++;
        if (multiSccCount < 2)
        {
            AssignDepthsBFS(cells, byId, startId);
            return;
        }

        // Map cell id → SCC index for fast lookup.
        var cellToScc = new Dictionary<string, int>();
        for (int i = 0; i < sccs.Count; i++)
            foreach (var c in sccs[i])
                cellToScc[c.State.Id] = i;

        // Determine the initial state's SCC (always row 0).
        int initialSccIdx = -1;
        if (cellToScc.TryGetValue(startId, out var initSi)) initialSccIdx = initSi;

        // Process SCCs in topological order (reverse of Tarjan output).
        // Initial SCC is forced first regardless.
        var processOrder = new List<int>();
        if (initialSccIdx >= 0) processOrder.Add(initialSccIdx);
        for (int i = sccs.Count - 1; i >= 0; i--)
            if (i != initialSccIdx) processOrder.Add(i);

        int row = 0;
        int singletonRow = -1;
        int singletonDepth = 0;
        foreach (var sccIdx in processOrder)
        {
            var scc = sccs[sccIdx];
            if (scc.Count >= 2)
            {
                AssignSccRow(scc, byId, cellToScc, sccIdx, startId, row);
                row++;
            }
            else
            {
                if (singletonRow == -1)
                {
                    singletonRow = row;
                    row++;
                }
                scc[0].Row = singletonRow;
                scc[0].Depth = singletonDepth++;
            }
        }

        // Hidden cells (and any orphan unset) → row 0 col 0
        foreach (var c in cells)
            if (c.Row == -1) { c.Row = 0; c.Depth = 0; }
    }

    /// <summary>
    /// Legacy BFS-based row/depth assignment (pre-SCC layout). Used as a
    /// fallback for monsters with at most one multi-cell SCC, where SCC
    /// layout's row split adds noise without benefit.
    /// </summary>
    private static void AssignDepthsBFS(List<Cell> cells, Dictionary<string, Cell> byId, string startId)
    {
        foreach (var c in cells) { c.Depth = 0; c.Row = -1; }

        AssignComponent(cells, byId, startId, row: 0);

        int nextRow = 1;
        int safetyRows = 0;
        while (safetyRows++ < 16)
        {
            Cell? unset = null;
            foreach (var c in cells)
            {
                if (c.Row == -1 && !c.IsHiddenInsideBox)
                {
                    unset = c;
                    break;
                }
            }
            if (unset == null) break;
            var seedId = PickComponentStarter(cells, unset);
            AssignComponent(cells, byId, seedId, nextRow);
            nextRow++;
        }

        foreach (var c in cells)
            if (c.Row == -1) { c.Row = 0; c.Depth = 0; }
    }

    /// <summary>
    /// Tarjan's strongly-connected components algorithm. Iterative form to
    /// avoid stack issues on large graphs (though state machines are small).
    /// Hidden cells are excluded from edges. Returns SCCs in REVERSE
    /// topological order (last-emitted SCC has no inter-SCC predecessors).
    /// </summary>
    private static List<List<Cell>> ComputeSCCs(List<Cell> visibleCells, Dictionary<string, Cell> byId)
    {
        var result = new List<List<Cell>>();
        int counter = 0;
        var idx = new Dictionary<string, int>();
        var low = new Dictionary<string, int>();
        var onStack = new HashSet<string>();
        var stack = new Stack<Cell>();

        void StrongConnect(Cell v)
        {
            idx[v.State.Id] = counter;
            low[v.State.Id] = counter;
            counter++;
            stack.Push(v);
            onStack.Add(v.State.Id);

            foreach (var nid in Successors(v.State))
            {
                if (string.IsNullOrEmpty(nid)) continue;
                if (!byId.TryGetValue(nid, out var w)) continue;
                if (w.IsHiddenInsideBox) continue;
                if (!idx.ContainsKey(w.State.Id))
                {
                    StrongConnect(w);
                    if (low[w.State.Id] < low[v.State.Id])
                        low[v.State.Id] = low[w.State.Id];
                }
                else if (onStack.Contains(w.State.Id))
                {
                    if (idx[w.State.Id] < low[v.State.Id])
                        low[v.State.Id] = idx[w.State.Id];
                }
            }

            if (low[v.State.Id] == idx[v.State.Id])
            {
                var component = new List<Cell>();
                Cell w;
                do
                {
                    w = stack.Pop();
                    onStack.Remove(w.State.Id);
                    component.Add(w);
                } while (w != v);
                result.Add(component);
            }
        }

        foreach (var v in visibleCells)
        {
            if (!idx.ContainsKey(v.State.Id))
                StrongConnect(v);
        }
        return result;
    }

    /// <summary>
    /// Assign row + depths to cells inside a multi-cell SCC. Picks an entry
    /// cell (initial state if present, else the cell with most external
    /// incoming edges) and runs BFS within the SCC to assign depths from 0.
    /// </summary>
    private static void AssignSccRow(List<Cell> scc, Dictionary<string, Cell> byId,
        Dictionary<string, int> cellToScc, int sccIdx, string startId, int row)
    {
        Cell entry = scc[0];
        // Prefer initial state if in this SCC.
        bool foundInit = false;
        foreach (var c in scc)
        {
            if (c.State.Id == startId) { entry = c; foundInit = true; break; }
        }
        if (!foundInit)
        {
            // Pick cell with most external (cross-SCC) incoming edges.
            int bestExt = -1;
            foreach (var c in scc)
            {
                int ext = 0;
                foreach (var kv in byId)
                {
                    var other = kv.Value;
                    if (other.IsHiddenInsideBox) continue;
                    if (cellToScc.TryGetValue(other.State.Id, out int oi) && oi == sccIdx) continue;
                    foreach (var nid in Successors(other.State))
                        if (nid == c.State.Id) ext++;
                }
                if (ext > bestExt) { bestExt = ext; entry = c; }
            }
        }

        var sccIds = new HashSet<string>();
        foreach (var c in scc) sccIds.Add(c.State.Id);

        var queue = new Queue<(string id, int d)>();
        queue.Enqueue((entry.State.Id, 0));
        var visited = new HashSet<string> { entry.State.Id };
        while (queue.Count > 0)
        {
            var (id, d) = queue.Dequeue();
            if (!byId.TryGetValue(id, out var c)) continue;
            c.Row = row;
            c.Depth = d;
            foreach (var nid in Successors(c.State))
            {
                if (string.IsNullOrEmpty(nid)) continue;
                if (!sccIds.Contains(nid)) continue;
                if (visited.Add(nid)) queue.Enqueue((nid, d + 1));
            }
        }
    }

    /// <summary>
    /// Assign depths to all cells reachable from `startId` within ONE component.
    /// Uses `Row` tag to prevent cross-row contamination. Combines forward
    /// visible-hop BFS + back-prop, then shifts the row's depths so min==0.
    /// Cells with `Row == -1` are "unassigned"; depth values are meaningless
    /// until Row gets set.
    /// </summary>
    private static void AssignComponent(List<Cell> cells, Dictionary<string, Cell> byId, string startId, int row)
    {
        if (!byId.TryGetValue(startId, out var startCell)) return;
        startCell.Depth = 0;
        startCell.Row = row;

        bool changed = true;
        int safety = 0;
        while (changed && safety++ < 32)
        {
            changed = false;
            foreach (var c in cells)
            {
                if (c.Row != row) continue;
                foreach (var nextId in Successors(c.State))
                {
                    if (string.IsNullOrEmpty(nextId)) continue;
                    if (!byId.TryGetValue(nextId, out var nc)) continue;
                    if (nc.Row >= 0 && nc.Row != row) continue; // belongs to another component
                    int nextD = c.Depth + (nc.IsHiddenInsideBox ? 0 : 1);
                    if (nc.Row == -1 || nextD < nc.Depth)
                    {
                        nc.Depth = nextD;
                        nc.Row = row;
                        changed = true;
                    }
                }
            }
        }

        // Back-propagate: assign depths to cells with no forward path from
        // start but a path TO some cell already in this row.
        safety = 0;
        changed = true;
        while (changed && safety++ < 32)
        {
            changed = false;
            foreach (var c in cells)
            {
                if (c.Row != -1) continue; // already assigned to some row
                int? derived = null;
                foreach (var nextId in Successors(c.State))
                {
                    if (string.IsNullOrEmpty(nextId)) continue;
                    if (!byId.TryGetValue(nextId, out var nc)) continue;
                    if (nc.Row != row) continue;
                    int cost = nc.IsHiddenInsideBox ? 0 : 1;
                    int d = nc.Depth - cost;
                    if (derived == null || d < derived.Value) derived = d;
                }
                if (derived != null)
                {
                    c.Depth = derived.Value;
                    c.Row = row;
                    changed = true;
                }
            }
        }

        // Shift this row's depths so min == 0 (back-prop may have produced
        // negatives). Include ALL cells in this row regardless of depth.
        var rowCells = cells.Where(c => c.Row == row).ToList();
        if (rowCells.Count > 0)
        {
            int minD = rowCells.Min(c => c.Depth);
            if (minD != 0)
                foreach (var c in rowCells) c.Depth -= minD;
        }
    }

    /// <summary>
    /// Pick a "good" starter for an unassigned component: prefer a cell with
    /// no incoming followup from other unassigned cells (a true chain head).
    /// Falls back to the given cell if no clean head exists.
    /// </summary>
    private static string PickComponentStarter(List<Cell> cells, Cell fallback)
    {
        var unsetIds = new HashSet<string>();
        foreach (var c in cells) if (c.Row == -1) unsetIds.Add(c.State.Id);

        Cell? best = null;
        foreach (var c in cells)
        {
            if (c.Row != -1 || c.IsHiddenInsideBox) continue;
            bool hasIncomingFromUnset = false;
            foreach (var other in cells)
            {
                if (other == c) continue;
                if (!unsetIds.Contains(other.State.Id)) continue;
                if (other.State.FollowUpStateId == c.State.Id)
                {
                    hasIncomingFromUnset = true;
                    break;
                }
            }
            if (!hasIncomingFromUnset)
            {
                best = c;
                break;
            }
        }
        return (best ?? fallback).State.Id;
    }

    private static List<Cell> BfsOrder(List<Cell> cells, Dictionary<string, Cell> byId, string startId)
    {
        var order = new List<Cell>();
        var visited = new HashSet<string> { startId };
        var queue = new Queue<string>();
        queue.Enqueue(startId);
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (!byId.TryGetValue(id, out var c)) continue;
            order.Add(c);
            foreach (var n in Successors(c.State))
            {
                if (string.IsNullOrEmpty(n)) continue;
                if (visited.Add(n)) queue.Enqueue(n);
            }
        }
        foreach (var c in cells)
            if (!visited.Contains(c.State.Id)) order.Add(c);
        return order;
    }

    private static IEnumerable<string> Successors(MonsterIntentMetadata.StateInfo s)
    {
        if (!string.IsNullOrEmpty(s.FollowUpStateId))
            yield return s.FollowUpStateId!;
        foreach (var b in s.Branches)
            if (!string.IsNullOrEmpty(b.TargetStateId))
                yield return b.TargetStateId;
    }

    // ── Cell rendering ──────────────────────────────────────

    private static Control BuildCellNode(Cell c, Dictionary<string, Cell> byId, string monsterId)
    {
        if (c.State.Kind == MonsterIntentMetadata.StateKind.RandomBranch
         || c.State.Kind == MonsterIntentMetadata.StateKind.ConditionalBranch)
        {
            return BuildGroupBoxCell(c, byId, monsterId);
        }
        return BuildMoveCell(c);
    }

    /// <summary>
    /// Multi-intent MoveState: each intent rendered as icon + damage-below,
    /// horizontally inside one cell. If this cell has a distributed-initial
    /// label (phase 1 conditional unfolding or phase 2 variants table), a
    /// small gold "X号位初始"-style label is drawn in the top-left and the
    /// icon content is shifted down to make room.
    /// </summary>
    private static Control BuildMoveCell(Cell c)
    {
        var holder = new Control { Name = "Cell_" + c.State.Id };
        holder.MouseFilter = Control.MouseFilterEnum.Ignore;

        int topOff = 0;
        if (_initialLabels != null && _initialLabels.TryGetValue(c.State.Id, out var initLbl))
        {
            var lbl = new Label { Text = initLbl };
            lbl.AddThemeColorOverride("font_color", GoldLabel);
            lbl.AddThemeFontSizeOverride("font_size", ProbFont);
            lbl.Position = new Vector2(2, 0);
            lbl.Size = new Vector2(c.Width - 4, ProbFont + 4);
            lbl.MouseFilter = Control.MouseFilterEnum.Ignore;
            holder.AddChild(lbl);
            topOff = ProbFont + 4;
        }

        RenderMoveContentInto(holder, c.State, c.Width, c.Height - topOff, topOffset: topOff);
        return holder;
    }

    /// <summary>
    /// Render a Move state's icon row + damage labels into the given parent
    /// Control. Used by both standalone Move cells and group-box mini cells.
    /// </summary>
    private static void RenderMoveContentInto(
        Control parent,
        MonsterIntentMetadata.StateInfo state,
        int width, int height, int topOffset)
    {
        var intents = state.Intents;
        int n = intents.Count;
        if (n == 0) return;

        int totalIconW = n * IconSize + (n - 1) * IntraIconGap;
        int startX = (width - totalIconW) / 2;

        for (int i = 0; i < n; i++)
        {
            var info = intents[i];
            int x = startX + i * (IconSize + IntraIconGap);

            var iconNode = BuildIntentIcon(info);
            iconNode.Position = new Vector2(x, topOffset + 2);
            iconNode.Size = new Vector2(IconSize, IconSize);
            parent.AddChild(iconNode);

            string? text = BuildIntentNumber(info);
            if (!string.IsNullOrEmpty(text))
            {
                var lbl = new Label { Text = text };
                lbl.AddThemeColorOverride("font_color", CreamLabel);
                lbl.AddThemeFontSizeOverride("font_size", LabelFont);
                lbl.HorizontalAlignment = HorizontalAlignment.Center;
                lbl.Position = new Vector2(x - 4, topOffset + IconSize + 2);
                lbl.Size = new Vector2(IconSize + 8, height - IconSize - 2);
                parent.AddChild(lbl);
            }
        }
    }

    /// <summary>
    /// Cyan-bordered group box. Each branch row mirrors its target Move state
    /// (full multi-intent rendering), with a probability% / "条件" label
    /// overlaid in the top-left of the row.
    /// </summary>
    private static Control BuildGroupBoxCell(Cell c, Dictionary<string, Cell> byId, string monsterId)
    {
        var box = new Panel { Name = "Group_" + c.State.Id };
        var style = new StyleBoxFlat
        {
            BgColor = GroupBg,
            BorderColor = GroupBorder,
            BorderWidthLeft = GroupBoxBorder,
            BorderWidthRight = GroupBoxBorder,
            BorderWidthTop = GroupBoxBorder,
            BorderWidthBottom = GroupBoxBorder,
            CornerRadiusTopLeft = GroupBoxRadius,
            CornerRadiusTopRight = GroupBoxRadius,
            CornerRadiusBottomLeft = GroupBoxRadius,
            CornerRadiusBottomRight = GroupBoxRadius,
        };
        box.AddThemeStyleboxOverride("panel", style);
        box.MouseFilter = Control.MouseFilterEnum.Ignore;

        // Round 9 round 25: phase-2 distributed-initial label on the box
        // itself (e.g. Exoskeleton's RAND box gets "4号位初始" at top-left).
        // Shift the internal rowY start to make room.
        int initialTopOffset = 0;
        if (_initialLabels != null && _initialLabels.TryGetValue(c.State.Id, out var boxInitLbl))
        {
            var lbl = new Label { Text = boxInitLbl };
            lbl.AddThemeColorOverride("font_color", GoldLabel);
            lbl.AddThemeFontSizeOverride("font_size", ProbFont);
            lbl.Position = new Vector2(GroupBoxBorder + 2, GroupBoxBorder);
            lbl.Size = new Vector2(c.Width - 2 * GroupBoxBorder - 4, ProbFont + 4);
            lbl.MouseFilter = Control.MouseFilterEnum.Ignore;
            box.AddChild(lbl);
            initialTopOffset = ProbFont + 4;
        }

        var branches = c.State.Branches;
        if (branches.Count == 0) return box;

        bool conditional = c.State.Kind == MonsterIntentMetadata.StateKind.ConditionalBranch;
        float totalWeight = branches.Sum(b => b.Weight);
        if (totalWeight <= 0f) totalWeight = 1f;

        int innerLeft = GroupBoxBorder + GroupBoxPad;
        int rowY = GroupBoxBorder + GroupBoxPad + initialTopOffset;

        for (int i = 0; i < branches.Count; i++)
        {
            var b = branches[i];
            int rowW = CellWidth;
            int rowH = CellHeight;
            MonsterIntentMetadata.StateInfo? targetState = null;
            if (!string.IsNullOrEmpty(b.TargetStateId)
                && byId.TryGetValue(b.TargetStateId, out var t))
            {
                rowW = t.Width;
                rowH = t.Height;
                targetState = t.State;
            }

            // Mini-cell holder for this branch row.
            var rowHolder = new Control { Name = "Branch_" + i };
            rowHolder.Position = new Vector2(innerLeft, rowY);
            rowHolder.Size = new Vector2(rowW, rowH);
            rowHolder.MouseFilter = Control.MouseFilterEnum.Ignore;
            box.AddChild(rowHolder);

            // Render target's content into the row. For Move targets we render
            // the icon row directly; for nested group boxes (RandomBranch /
            // ConditionalBranch) we recursively call BuildGroupBoxCell so the
            // inner cyan box appears inside this row. Round 9 round 19: this
            // is the canonical case for monsters like Fabricator whose
            // ConditionalBranchState wraps a RandomBranchState as one branch.
            if (targetState != null)
            {
                if (targetState.Kind == MonsterIntentMetadata.StateKind.Move)
                {
                    RenderMoveContentInto(rowHolder, targetState, rowW, rowH, topOffset: 0);
                }
                else if (targetState.Kind == MonsterIntentMetadata.StateKind.RandomBranch
                      || targetState.Kind == MonsterIntentMetadata.StateKind.ConditionalBranch)
                {
                    var nestedCell = byId[targetState.Id];
                    var nestedBox = BuildGroupBoxCell(nestedCell, byId, monsterId);
                    nestedBox.Position = Vector2.Zero;
                    nestedBox.Size = new Vector2(rowW, rowH);
                    rowHolder.AddChild(nestedBox);
                }
            }

            // Top-left overlay: probability or condition.
            // Round 9 round 12: for conditional branches we look up a per-monster
            // human-readable hint table (`MonsterConditionHints`); fall back to
            // generic "条件" when the monster isn't documented.
            // Round 9 round 17: for random branches we render the bare lambda
            // percentage AND a structural-rule annotation (≤1 / ≤N / ×1 / CD:N)
            // — STS1 mod style — instead of trying to match the runtime picker.
            string label;
            if (conditional)
            {
                label = MonsterConditionHints.Get(monsterId, i) ?? "条件";
            }
            else
            {
                float pct = b.Weight / totalWeight * 100f;
                label = $"{pct:F0}%";
                var ann = BuildBranchAnnotation(b);
                if (!string.IsNullOrEmpty(ann)) label += " " + ann;
            }
            var probLbl = new Label { Text = label };
            probLbl.AddThemeColorOverride("font_color", GoldLabel);
            probLbl.AddThemeFontSizeOverride("font_size", ProbFont);
            probLbl.Position = new Vector2(2, 0);
            probLbl.Size = new Vector2(rowW - 4, ProbFont + 4);
            rowHolder.AddChild(probLbl);

            rowY += rowH;
            if (i < branches.Count - 1) rowY += RowSeparator;
        }
        return box;
    }

    /// <summary>
    /// Round 9 round 17: STS1-style structural annotation for a RandomBranch
    /// branch row, rendered next to the probability%. Captures the four
    /// repetition rules from `RandomBranchState.StateWeight`:
    ///   - CannotRepeat        → "≤1"  (can't fire twice in a row)
    ///   - CanRepeatXTimes(N)  → "≤N"  (max N times in a row)
    ///   - UseOnlyOnce         → "×1"  (one-shot per combat)
    ///   - cooldown=N          → "CD{N}" (locked out for N moves after firing)
    /// Multiple annotations join with a space; CanRepeatForever returns empty.
    /// </summary>
    private static string BuildBranchAnnotation(MonsterIntentMetadata.BranchInfo b)
    {
        var parts = new List<string>(2);
        switch (b.RepeatType)
        {
            case MegaCrit.Sts2.Core.MonsterMoves.MoveRepeatType.CannotRepeat:
                parts.Add("≤1");
                break;
            case MegaCrit.Sts2.Core.MonsterMoves.MoveRepeatType.CanRepeatXTimes:
                if (b.MaxTimes > 0) parts.Add($"≤{b.MaxTimes}");
                break;
            case MegaCrit.Sts2.Core.MonsterMoves.MoveRepeatType.UseOnlyOnce:
                parts.Add("×1");
                break;
        }
        if (b.Cooldown > 0) parts.Add($"CD{b.Cooldown}");
        return parts.Count == 0 ? "" : string.Join(" ", parts);
    }

    private static Control BuildIntentIcon(MonsterIntentMetadata.IntentInfo info)
    {
        try
        {
            if (info.IntentInstance != null)
            {
                var tex = IntentIconCache.GetIcon(info.IntentInstance, info.Damage ?? 0);
                if (tex != null && Godot.GodotObject.IsInstanceValid(tex))
                {
                    var rect = new TextureRect
                    {
                        Texture = tex,
                        ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                        StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                        MouseFilter = Control.MouseFilterEnum.Ignore,
                    };
                    return rect;
                }
            }
        }
        catch (System.Exception ex)
        {
            Safe.Warn($"[IntentPanel] BuildIntentIcon failed for {info.IntentTypeName}: {ex.Message}");
        }
        var fallback = new Label { Text = (info.IntentTypeName.Length > 0 ? info.IntentTypeName[..1] : "?") };
        fallback.AddThemeColorOverride("font_color", CreamLabel);
        fallback.AddThemeFontSizeOverride("font_size", 28);
        fallback.HorizontalAlignment = HorizontalAlignment.Center;
        fallback.VerticalAlignment = VerticalAlignment.Center;
        return fallback;
    }

    private static string? BuildIntentNumber(MonsterIntentMetadata.IntentInfo info)
    {
        if (info.Damage.HasValue && info.Damage.Value > 0)
        {
            return info.Repeats > 1
                ? $"{info.Damage}×{info.Repeats}"
                : info.Damage.ToString();
        }
        return null;
    }

    private static string TryName(MegaCrit.Sts2.Core.Models.MonsterModel monster, string fallback)
    {
        try { return monster.Title?.GetFormattedText() ?? fallback; }
        catch { return fallback; }
    }
}
