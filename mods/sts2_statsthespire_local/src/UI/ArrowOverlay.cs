using System.Collections.Generic;
using CommunityStats.Util;
using Godot;

namespace CommunityStats.UI;

/// <summary>
/// Builds arrows between IntentStateMachinePanel state cells as a set of
/// Line2D + Polygon2D child nodes with **orthogonal (manhattan) routing** —
/// never diagonal.
///
/// **Round 9 round 9 rewrite** per PRD §3.10.3:
///   - Forward edges in the same row use a single horizontal segment.
///   - Forward edges crossing rows use a 3-segment manhattan path
///     (horizontal → vertical → horizontal).
///   - Back-edges (target depth ≤ source depth) route around either above or
///     below the cell row using a 5-segment U-shape.
///   - All segments are axis-aligned. No diagonals.
///   - Conditional branches are red, all others gold.
///
/// **Why not _Draw**: this project's csproj references GodotSharp.dll directly
/// without the source generator, so any `_Draw` override silently fails to
/// register. We render via built-in Line2D / Polygon2D children which don't
/// need the source generator. See history at git log :: round 9 round 7.
/// </summary>
public partial class ArrowOverlay : Control
{
    private static readonly Color GoldArrow = new("#EFC851");
    private static readonly Color RedArrow  = new("#FF4545");
    // Round 9 round 12: stroke +5 px (2.5 → 7.5) per user feedback for visibility
    // Round 9 round 13: arrow head triangle 2× (12 → 24) per user feedback
    // Round 9 round 21: arrow head -25% (24 → 18) per user feedback
    private const float ArrowStroke = 7.5f;
    private const float ArrowHead   = 18f;
    // Round 9 round 18: HBend bumped 12 → 36 so back-edge "exit" stub is long
    // enough that the post-corner segment (before the arrowhead at the dest
    // side) doesn't get swallowed by the triangle. See comment in HGutter.
    // Round 9 round 21: HBend bumped further 36 → 48 so back-edge brackets
    // visibly extend past the cells (per user feedback that the right/down/
    // left bend was too narrow).
    private const float HBend       = 48f; // distance to walk horizontally before turning vertical
    private const float ChannelGap  = 18f; // back-edge routing channel above / below cells
    // Round 9 round 31: per-slot offsets for overlap resolution. Each conflict
    // pushes the edge one slot, which lifts channelY by SlotChannelStep and
    // extends bendX by SlotBendStep. Both shifts together fully separate two
    // otherwise-overlapping elevated edges.
    private const float SlotChannelStep = 14f;
    private const float SlotBendStep    = 14f;

    // Round 9 round 18: how much visible horizontal line should sit between
    // the manhattan corner and the arrowhead's base on the destination side.
    // Used by both forward 3-segment and back-edge 5-segment routing.
    private const float PostBendVisible = 16f;

    public void Build(
        List<IntentStateMachinePanel.Cell> cells,
        Dictionary<string, IntentStateMachinePanel.Cell> byId,
        Dictionary<string, Rect2> cellRects,
        Dictionary<string, List<Rect2>> miniCellRects)
    {
        Safe.Info($"[ArrowOverlay] Build: cells={cells.Count} rects={cellRects.Count} minis={miniCellRects.Count}");

        // Round 9 round 30: obstacle rects = ALL visible standalone cell rects,
        // used to (a) force channelY above any rect the path crosses, (b)
        // detect cross-cell collisions in forward routing.
        // boxRectList is the subset that are Random/Conditional branch boxes,
        // used to decide whether a given edge's src or dst is a box (top-exit
        // / top-entry routing instead of mid-right / mid-left).
        var obstacleRects = new List<Rect2>();
        var boxRectList = new List<Rect2>();
        foreach (var kv in cellRects)
        {
            obstacleRects.Add(kv.Value);
            if (!byId.TryGetValue(kv.Key, out var bc)) continue;
            if (bc.State.Kind == MonsterIntentMetadata.StateKind.RandomBranch
             || bc.State.Kind == MonsterIntentMetadata.StateKind.ConditionalBranch)
                boxRectList.Add(kv.Value);
        }

        // Round 9 round 26: merge standalone cellRects + per-box miniCellRects
        // into `allRects` (cell id → list of all rects).
        var allRects = new Dictionary<string, List<Rect2>>();
        foreach (var kv in cellRects)
        {
            if (!allRects.TryGetValue(kv.Key, out var list))
            {
                list = new List<Rect2>();
                allRects[kv.Key] = list;
            }
            list.Add(kv.Value);
        }
        foreach (var kv in miniCellRects)
        {
            if (!allRects.TryGetValue(kv.Key, out var list))
            {
                list = new List<Rect2>();
                allRects[kv.Key] = list;
            }
            foreach (var r in kv.Value) list.Add(r);
        }

        // Round 9 round 28 fallback: for hidden cells with no rect of their
        // own (e.g. phase-1 hidden INIT_MOVE), resolve via parent chain so
        // edges pointing to them (SPAWNED.followup → INIT_MOVE) still land
        // on a visible rect (NASTY_BITE standalone, the redirected target).
        foreach (var c in cells)
        {
            if (allRects.ContainsKey(c.State.Id)) continue;
            if (!c.IsHiddenInsideBox) continue;
            var resolved = ResolveVisible(c, byId);
            if (resolved.State.Id == c.State.Id) continue; // no progress
            if (allRects.TryGetValue(resolved.State.Id, out var rr))
                allRects[c.State.Id] = new List<Rect2>(rr);
        }

        // Round 9 round 28: box-level self-loop merge. Detect boxes where
        // ALL branch targets' followups loop back to this box — in that case
        // draw ONE box-level self-loop instead of N tiny mini-cell brackets.
        // HauntedShip is the canonical case: all 4 branches (ramming/swipe/
        // stomp/haunt) have followup = rand, so without merging we'd get 4
        // self-loop brackets inside rand.
        var mergedSelfLoopBoxes = new HashSet<string>();
        foreach (var c in cells)
        {
            if (c.IsHiddenInsideBox) continue;
            if (c.State.Kind != MonsterIntentMetadata.StateKind.RandomBranch
             && c.State.Kind != MonsterIntentMetadata.StateKind.ConditionalBranch)
                continue;
            if (c.State.Branches.Count == 0) continue;
            bool allSelfLoop = true;
            foreach (var b in c.State.Branches)
            {
                if (string.IsNullOrEmpty(b.TargetStateId)) { allSelfLoop = false; break; }
                if (!byId.TryGetValue(b.TargetStateId, out var branchCell)) { allSelfLoop = false; break; }
                if (branchCell.State.FollowUpStateId != c.State.Id)
                {
                    allSelfLoop = false;
                    break;
                }
            }
            if (allSelfLoop) mergedSelfLoopBoxes.Add(c.State.Id);
        }

        // Pre-pass: detect cells whose standalone rect has at least one
        // non-self-loop outgoing edge. Used to skip drawing redundant
        // standalone self-loops when a real exit arrow exists
        // (Toadpole / Fogmog). Mini-cell self-loops are not subject to this
        // check — they're always drawn because they carry distinct info.
        var hasNonSelfOutStandalone = new HashSet<string>();
        foreach (var rawSrc in cells)
        {
            if (!cellRects.TryGetValue(rawSrc.State.Id, out var stdSrcRect)) continue;
            foreach (var (targetId, _) in EdgesOf(rawSrc.State))
            {
                if (!byId.TryGetValue(targetId, out var dst)) continue;
                if (!cellRects.TryGetValue(dst.State.Id, out var stdDstRect)) continue;
                if (rawSrc.State.Id != dst.State.Id)
                    hasNonSelfOutStandalone.Add(rawSrc.State.Id);
            }
        }

        int edgeCount = 0;
        var drawn = new HashSet<(string, double, double, string, double, double)>();

        // Round 9 round 30: collect non-self-loop edges first, then group by
        // (dstRect, color) so multiple arrows hitting the same destination can
        // share a trunk. Self-loops render immediately since they don't merge.
        var pendingEdges = new List<(Rect2 srcRect, Rect2 dstRect,
            IntentStateMachinePanel.Cell srcCell, IntentStateMachinePanel.Cell dstCell, Color color)>();

        foreach (var rawSrc in cells)
        {
            // Round 9 round 28: phase-1 hidden ConditionalBranch cells (e.g.
            // Wriggler's INIT_MOVE after phase 1 hide) should NOT emit their
            // branch edges. Their conditions are distributed as initial
            // labels on target cells; emitting branches again would produce
            // duplicate / confusing arrows from the fallback-resolved rect.
            if (rawSrc.IsHiddenInsideBox
                && rawSrc.State.Kind == MonsterIntentMetadata.StateKind.ConditionalBranch)
                continue;

            if (!allRects.TryGetValue(rawSrc.State.Id, out var srcRects)) continue;

            foreach (var (targetId, isConditional) in EdgesOf(rawSrc.State))
            {
                if (!byId.TryGetValue(targetId, out var dst)) continue;
                if (!allRects.TryGetValue(targetId, out var dstRects)) continue;

                var color = isConditional ? RedArrow : GoldArrow;

                foreach (var srcRect in srcRects)
                {
                    bool isStandaloneSrc =
                        cellRects.TryGetValue(rawSrc.State.Id, out var stdSrc)
                        && RectEquals(stdSrc, srcRect);

                    foreach (var dstRect in dstRects)
                    {
                        bool isSelfLoop = RectEquals(srcRect, dstRect)
                                       || RectContainsCenter(dstRect, srcRect);

                        // Round 9 round 29: re-enable non-self-loop mini-cell
                        // arrows (reverting round 27). Each mini-cell that
                        // has a followup to an external cell now gets its
                        // own arrow, which is what the user expects for
                        // Exoskeleton (MANDIBLE mini → ENRAGE) and Inklet
                        // (PIERCING_GAZE mini → JAB). The ScrollOfBiting
                        // overlap concern is mitigated by other routing
                        // rules (dedup, merge self-loops).

                        // Round 9 round 31: when dst has a standalone rect,
                        // always route to the standalone rect — skip any
                        // non-standalone dst appearances. This covers both
                        // std→mini (prefer std→std) and mini→mini (prefer
                        // mini→std). Mini→mini within the same container box
                        // was creating phantom intra-box arrows (Exoskeleton
                        // mini SKITTER → mini MANDIBLE downward arrowhead).
                        if (cellRects.TryGetValue(dst.State.Id, out var stdDst)
                            && !RectEquals(stdDst, dstRect))
                            continue;

                        var key = (rawSrc.State.Id, srcRect.Position.X, srcRect.Position.Y,
                                   dst.State.Id, dstRect.Position.X, dstRect.Position.Y);
                        if (!drawn.Add(key)) continue;

                        if (isSelfLoop)
                        {
                            if (isStandaloneSrc
                                && hasNonSelfOutStandalone.Contains(rawSrc.State.Id))
                                continue;
                            // Round 9 round 29: if THIS mini-cell self-loop is
                            // inside a box whose merge rule is active, skip.
                            // Works for BOTH hidden-src (ramming/swipe/haunt
                            // with ParentBoxStateId = rand) AND visible-src
                            // whose mini-cell happens to sit inside a merge
                            // box (HauntedShip's `ramming` is the initial,
                            // visible standalone, but also a mini-cell inside
                            // `rand` which is merged — without this extra
                            // skip we'd get 2 self-loops).
                            if (!isStandaloneSrc
                                && mergedSelfLoopBoxes.Contains(dst.State.Id))
                                continue;
                            AddSelfLoop(srcRect, color);
                        }
                        else
                        {
                            pendingEdges.Add((srcRect, dstRect, rawSrc, dst, color));
                        }
                        edgeCount++;
                    }
                }
            }
        }

        // Round 9 round 31: per-edge "container box" tag (mini-cell → box id).
        // Used during elevated routing to exclude the containing box from the
        // forward crossesObstacle check. Center-point containment is tolerant
        // to the ~8 px overhang from ComputeBoxSize using pre-inflation target
        // heights.
        var edgeBoxId = new string?[pendingEdges.Count];
        for (int i = 0; i < pendingEdges.Count; i++)
        {
            var e = pendingEdges[i];
            if (cellRects.TryGetValue(e.srcCell.State.Id, out var ownStd)
                && RectEquals(ownStd, e.srcRect))
                continue;
            foreach (var kv in cellRects)
            {
                if (kv.Key == e.srcCell.State.Id) continue;
                if (RectEquals(kv.Value, e.srcRect)) continue;
                if (RectContainsCenter(kv.Value, e.srcRect))
                {
                    edgeBoxId[i] = kv.Key;
                    break;
                }
            }
        }

        // Round 9 round 31: collapse mini-cell sources to their containing box
        // ONLY when every mini-cell edge originating from that box points to
        // the SAME external target. Inklet case: mini PIERCING_GAZE → JAB and
        // mini WHIRLWIND → JAB share target JAB → collapse to rand box → JAB
        // (then merges with WHIRLWIND std → JAB as trunk+secondary).
        // Exoskeleton case (post-unfold): mini SKITTER → MANDIBLE and mini
        // MANDIBLE → ENRAGE have different targets → no collapse, each mini
        // keeps its own src rect and the two back-edges slot-offset apart.
        // Round 9 round 31: pre-compute boxes that contain at least one mini
        // self-loop (a branch target whose FollowUp points back to the box).
        // Such boxes are NOT collapsible because the self-looping mini
        // represents a separate "stay here" branch, not a uniform exit.
        // Canonical case: Queen Box1 contains mini BURN_BRIGHT (FollowUp=Box1)
        // and mini ENRAGE (FollowUp=OFF). Even though only one external edge
        // (to OFF) exists, the box has a self-loop branch so the box is NOT
        // a single-decision-to-one-target.
        var boxesWithMiniSelfLoop = new HashSet<string>();
        foreach (var c in cells)
        {
            if (c.State.Kind != MonsterIntentMetadata.StateKind.ConditionalBranch
             && c.State.Kind != MonsterIntentMetadata.StateKind.RandomBranch)
                continue;
            foreach (var b in c.State.Branches)
            {
                if (string.IsNullOrEmpty(b.TargetStateId)) continue;
                if (!byId.TryGetValue(b.TargetStateId, out var target)) continue;
                if (target.State.FollowUpStateId == c.State.Id)
                {
                    boxesWithMiniSelfLoop.Add(c.State.Id);
                    break;
                }
            }
        }

        // Round 9 round 31: collapse requires ALL of:
        //   1. ≥2 mini external edges in the box
        //   2. all going to the same target
        //   3. box has NO mini self-loop branches
        var boxCollapsible = new Dictionary<string, (Rect2 dst, int count, bool ok)>();
        for (int i = 0; i < pendingEdges.Count; i++)
        {
            var boxId = edgeBoxId[i];
            if (boxId == null) continue;
            var e = pendingEdges[i];
            if (boxCollapsible.TryGetValue(boxId, out var entry))
            {
                if (!entry.ok) continue;
                if (!RectEquals(entry.dst, e.dstRect))
                    boxCollapsible[boxId] = (entry.dst, entry.count, false);
                else
                    boxCollapsible[boxId] = (entry.dst, entry.count + 1, true);
            }
            else
            {
                boxCollapsible[boxId] = (e.dstRect, 1, true);
            }
        }
        for (int i = 0; i < pendingEdges.Count; i++)
        {
            var boxId = edgeBoxId[i];
            if (boxId == null) continue;
            if (boxesWithMiniSelfLoop.Contains(boxId)) continue;
            if (!boxCollapsible.TryGetValue(boxId, out var entry) || !entry.ok) continue;
            if (entry.count < 2) continue; // require ≥2 minis sharing target
            if (!cellRects.TryGetValue(boxId, out var boxRect)) continue;
            var e = pendingEdges[i];
            pendingEdges[i] = (boxRect, e.dstRect, e.srcCell, e.dstCell, e.color);
            edgeBoxId[i] = null; // after collapse, no longer "inside" a container
        }

        // Dedup: identical (srcRect, dstRect, color) tuples are merged.
        var dedupKeys = new HashSet<(float, float, float, float, float, float, uint)>();
        var uniqueEdges = new List<(Rect2 srcRect, Rect2 dstRect,
            IntentStateMachinePanel.Cell srcCell, IntentStateMachinePanel.Cell dstCell,
            Color color, Rect2? containerBox)>();
        for (int i = 0; i < pendingEdges.Count; i++)
        {
            var e = pendingEdges[i];
            var k = (e.srcRect.Position.X, e.srcRect.Position.Y,
                     e.dstRect.Position.X, e.dstRect.Position.Y,
                     e.srcRect.Size.X, e.srcRect.Size.Y,
                     e.color.ToRgba32());
            if (!dedupKeys.Add(k)) continue;
            Rect2? container = null;
            if (edgeBoxId[i] != null
                && cellRects.TryGetValue(edgeBoxId[i]!, out var containerRect))
                container = containerRect;
            uniqueEdges.Add((e.srcRect, e.dstRect, e.srcCell, e.dstCell, e.color, container));
        }

        // Round 9 round 30: group by (dstRect, color). For groups ≥2, pick the
        // topmost+leftmost source as the MAIN TRUNK (drawn with standard routing
        // + arrowhead). Each other (secondary) source routes manhattan-style
        // to the closest point on the trunk's polyline with NO arrowhead — so
        // visually all branches feed into the same trunk line.
        // Round 9 round 30: group by (dstRect, color, isForward). Forward and
        // backward edges to the same dst must NOT merge, since their routings
        // differ fundamentally — merging them produces a weird trunk whose
        // secondary has to cross the panel.
        // Round 9 round 31: also split by mini-vs-non-mini source. A mini's
        // secondary path projected onto a std-trunk's polyline can land at
        // an X that lies inside an obstacle cell, drawing the secondary's
        // last horizontal segment THROUGH that obstacle (Queen mini ENRAGE →
        // std OFF secondary projecting onto std ENRAGE → std OFF trunk).
        // Keeping mini and std sources in separate groups makes each route
        // independently, with the mini source naturally elevating over
        // obstacles via BuildRoutedPath's forward-elevated branch.
        var groups = new Dictionary<(float, float, float, float, uint, bool, bool), List<int>>();
        for (int i = 0; i < uniqueEdges.Count; i++)
        {
            var e = uniqueEdges[i];
            bool fwd = e.dstRect.Position.X > e.srcRect.Position.X;
            bool isMiniSrc = e.containerBox.HasValue;
            var key = (e.dstRect.Position.X, e.dstRect.Position.Y,
                       e.dstRect.Size.X, e.dstRect.Size.Y, e.color.ToRgba32(), fwd, isMiniSrc);
            if (!groups.TryGetValue(key, out var list))
            {
                list = new List<int>();
                groups[key] = list;
            }
            list.Add(i);
        }

        // Round 9 round 31: defer all draw calls; collect primaries + attached
        // secondaries first. Then run overlap detection and assign slot
        // indices to re-route conflicting primaries. Finally draw everything.
        var primaries = new List<PendingPrimary>();
        var secondaries = new List<PendingSecondary>();

        foreach (var idxList in groups.Values)
        {
            if (idxList.Count == 1)
            {
                var e = uniqueEdges[idxList[0]];
                primaries.Add(new PendingPrimary
                {
                    Src = e.srcRect,
                    Dst = e.dstRect,
                    SrcIsBox = IsBoxRect(e.srcRect, boxRectList),
                    DstIsBox = IsBoxRect(e.dstRect, boxRectList),
                    ContainerBox = e.containerBox,
                    Color = e.color,
                    Path = null!,
                    Tip = default,
                    Secondaries = new List<int>(),
                });
                continue;
            }

            int trunkIdx = idxList[0];
            foreach (var i in idxList)
            {
                var cand = uniqueEdges[i];
                var cur = uniqueEdges[trunkIdx];
                float candMidY = cand.srcRect.Position.Y + cand.srcRect.Size.Y / 2f;
                float curMidY  = cur.srcRect.Position.Y + cur.srcRect.Size.Y / 2f;
                if (candMidY < curMidY - 0.1f) { trunkIdx = i; continue; }
                if (candMidY > curMidY + 0.1f) continue;
                float candMidX = cand.srcRect.Position.X + cand.srcRect.Size.X / 2f;
                float curMidX  = cur.srcRect.Position.X + cur.srcRect.Size.X / 2f;
                if (candMidX < curMidX) trunkIdx = i;
            }

            var trunkEdge = uniqueEdges[trunkIdx];
            int primaryIdx = primaries.Count;
            var secList = new List<int>();
            primaries.Add(new PendingPrimary
            {
                Src = trunkEdge.srcRect,
                Dst = trunkEdge.dstRect,
                SrcIsBox = IsBoxRect(trunkEdge.srcRect, boxRectList),
                DstIsBox = IsBoxRect(trunkEdge.dstRect, boxRectList),
                ContainerBox = trunkEdge.containerBox,
                Color = trunkEdge.color,
                Path = null!,
                Tip = default,
                Secondaries = secList,
            });
            foreach (var i in idxList)
            {
                if (i == trunkIdx) continue;
                var e = uniqueEdges[i];
                secList.Add(secondaries.Count);
                secondaries.Add(new PendingSecondary
                {
                    Src = e.srcRect,
                    Color = e.color,
                    TrunkIdx = primaryIdx,
                });
            }
        }

        // Build initial paths with slot=0.
        for (int i = 0; i < primaries.Count; i++)
        {
            var p = primaries[i];
            p.Path = BuildRoutedPath(p.Src, p.Dst, p.SrcIsBox, p.DstIsBox,
                obstacleRects, p.ContainerBox, p.SlotIdx, out var pTip);
            p.Tip = pTip;
            primaries[i] = p;
        }

        // Overlap resolution: for each primary after the first, check colinear
        // overlap against prior primaries. If found, bump slot and rebuild.
        // Iterate a few times in case a bump introduces a new conflict.
        for (int iter = 0; iter < 4; iter++)
        {
            bool changed = false;
            for (int i = 0; i < primaries.Count; i++)
            {
                for (int j = 0; j < i; j++)
                {
                    if (PathsColinearOverlap(primaries[i].Path, primaries[j].Path))
                    {
                        var p = primaries[i];
                        if (p.SlotIdx >= 6) break; // safety cap
                        p.SlotIdx++;
                        p.Path = BuildRoutedPath(p.Src, p.Dst, p.SrcIsBox, p.DstIsBox,
                            obstacleRects, p.ContainerBox, p.SlotIdx, out var pTip);
                        p.Tip = pTip;
                        primaries[i] = p;
                        changed = true;
                        break;
                    }
                }
            }
            if (!changed) break;
        }

        // Draw finalized primaries.
        foreach (var p in primaries)
        {
            AddLine(p.Path, p.Color);
            AddArrowHead(p.Path[p.Path.Length - 1], p.Tip, p.Color);
        }
        // Draw secondaries using their trunk's final path.
        foreach (var s in secondaries)
        {
            var trunkPath = primaries[s.TrunkIdx].Path;
            var secPath = BuildSecondaryMergePath(s.Src, trunkPath);
            AddLine(secPath, s.Color);
        }

        // Round 9 round 28: draw merged box-level self-loops after the main
        // pass. For boxes where ALL branches loop back to the box itself,
        // one single self-loop bracket on the right of the whole box replaces
        // N tiny mini-cell brackets.
        foreach (var boxId in mergedSelfLoopBoxes)
        {
            if (!cellRects.TryGetValue(boxId, out var boxRect)) continue;
            AddSelfLoop(boxRect, GoldArrow);
            edgeCount++;
        }

        Safe.Info($"[ArrowOverlay] Build: created {edgeCount} arrows");
    }

    private static bool RectEquals(Rect2 a, Rect2 b)
    {
        return Mathf.IsEqualApprox(a.Position.X, b.Position.X)
            && Mathf.IsEqualApprox(a.Position.Y, b.Position.Y)
            && Mathf.IsEqualApprox(a.Size.X, b.Size.X)
            && Mathf.IsEqualApprox(a.Size.Y, b.Size.Y);
    }

    private static bool RectContainsCenter(Rect2 outer, Rect2 inner)
    {
        float cx = inner.Position.X + inner.Size.X / 2f;
        float cy = inner.Position.Y + inner.Size.Y / 2f;
        return outer.Position.X <= cx
            && cx <= outer.Position.X + outer.Size.X
            && outer.Position.Y <= cy
            && cy <= outer.Position.Y + outer.Size.Y;
    }

    private static bool RectContains(Rect2 outer, Rect2 inner)
    {
        return outer.Position.X <= inner.Position.X + 0.1f
            && outer.Position.Y <= inner.Position.Y + 0.1f
            && outer.Position.X + outer.Size.X >= inner.Position.X + inner.Size.X - 0.1f
            && outer.Position.Y + outer.Size.Y >= inner.Position.Y + inner.Size.Y - 0.1f;
    }

    /// <summary>
    /// Walk up the parent-box chain until we find a visible cell. Used so
    /// edges originating in or pointing to deeply-nested branch targets land
    /// on the outermost visible box (Fabricator's FABRICATE → random →
    /// conditional case). Returns the input cell if it's already visible or
    /// has no parent.
    /// </summary>
    private static IntentStateMachinePanel.Cell ResolveVisible(
        IntentStateMachinePanel.Cell c,
        Dictionary<string, IntentStateMachinePanel.Cell> byId)
    {
        int safety = 0;
        while (c.IsHiddenInsideBox && c.ParentBoxStateId != null && safety++ < 8)
        {
            if (!byId.TryGetValue(c.ParentBoxStateId, out var parent)) break;
            c = parent;
        }
        return c;
    }

    private static IEnumerable<(string targetId, bool isConditional)> EdgesOf(
        MonsterIntentMetadata.StateInfo s)
    {
        // Round 9 round 13: only emit FollowUp edges for RandomBranch states.
        // Their branches are container-relationships and the targets are all
        // hidden inside the box, so redundant arrows are skipped.
        // Round 9 round 31: ConditionalBranch is ALSO suppressed for branch
        // emissions. The box renders with its branches as mini-cells labelled
        // via MonsterConditionHints ("计数<3", "计数≥3", etc), which already
        // expresses the "when condition X → target" semantics. The explicit
        // red back-edges from box → each target duplicate that information
        // and cluster badly for monsters where targets sit far away (Knowledge
        // Demon: 2 conditional back-edges + 2 mini-followup back-edges all
        // crowded into the same channel).
        if (!string.IsNullOrEmpty(s.FollowUpStateId))
            yield return (s.FollowUpStateId!, false);
    }

    private void AddRoutedArrow(Rect2 src, Rect2 dst,
        IntentStateMachinePanel.Cell srcCell, IntentStateMachinePanel.Cell dstCell, Color color,
        List<Rect2> obstacleRects, List<Rect2> boxRectList)
    {
        bool srcIsBox = IsBoxRect(src, boxRectList);
        bool dstIsBox = IsBoxRect(dst, boxRectList);
        var path = BuildRoutedPath(src, dst, srcIsBox, dstIsBox, obstacleRects, null, 0, out var tip);
        AddLine(path, color);
        AddArrowHead(path[path.Length - 1], tip, color);
    }

    private static bool IsBoxRect(Rect2 r, List<Rect2> boxes)
    {
        foreach (var b in boxes)
            if (RectEquals(b, r)) return true;
        return false;
    }

    private struct PendingPrimary
    {
        public Rect2 Src;
        public Rect2 Dst;
        public bool SrcIsBox;
        public bool DstIsBox;
        public Rect2? ContainerBox;
        public Color Color;
        public Vector2[] Path;
        public Vector2 Tip;
        public int SlotIdx;
        public List<int> Secondaries;
    }

    private struct PendingSecondary
    {
        public Rect2 Src;
        public Color Color;
        public int TrunkIdx;
    }

    /// <summary>
    /// Return true if any horizontal or vertical segment of `a` shares a
    /// colinear overlap with any segment of `b`. Used by the overlap
    /// resolution pass to detect when two independent primary edges would
    /// visually superimpose on each other.
    ///
    /// The final segment of each path (the "dst entry stub" that lands on
    /// dst.X - HeadCover) is EXCLUDED from the check. Two edges entering the
    /// same dst from the same side share this stub by geometric necessity —
    /// that's natural convergence, not a routing conflict.
    /// </summary>
    private static bool PathsColinearOverlap(Vector2[] a, Vector2[] b)
    {
        int aEnd = a.Length - 2; // skip last segment (index a.Length - 2)
        int bEnd = b.Length - 2;
        for (int i = 0; i < aEnd; i++)
        {
            var a1 = a[i]; var a2 = a[i + 1];
            for (int j = 0; j < bEnd; j++)
            {
                var b1 = b[j]; var b2 = b[j + 1];
                if (SegColinearOverlap(a1, a2, b1, b2)) return true;
            }
        }
        return false;
    }

    private static bool SegColinearOverlap(Vector2 a1, Vector2 a2, Vector2 b1, Vector2 b2)
    {
        bool aH = Mathf.IsEqualApprox(a1.Y, a2.Y);
        bool bH = Mathf.IsEqualApprox(b1.Y, b2.Y);
        if (aH && bH && Mathf.Abs(a1.Y - b1.Y) < 1.5f)
        {
            float aL = Mathf.Min(a1.X, a2.X), aR = Mathf.Max(a1.X, a2.X);
            float bL = Mathf.Min(b1.X, b2.X), bR = Mathf.Max(b1.X, b2.X);
            float overlap = Mathf.Min(aR, bR) - Mathf.Max(aL, bL);
            return overlap > 4f;
        }
        bool aV = Mathf.IsEqualApprox(a1.X, a2.X);
        bool bV = Mathf.IsEqualApprox(b1.X, b2.X);
        if (aV && bV && Mathf.Abs(a1.X - b1.X) < 1.5f)
        {
            float aT = Mathf.Min(a1.Y, a2.Y), aB = Mathf.Max(a1.Y, a2.Y);
            float bT = Mathf.Min(b1.Y, b2.Y), bB = Mathf.Max(b1.Y, b2.Y);
            float overlap = Mathf.Min(aB, bB) - Mathf.Max(aT, bT);
            return overlap > 4f;
        }
        return false;
    }

    /// <summary>
    /// Build the manhattan-routed polyline for a single edge. For back-edges
    /// (dst left of src), the channel Y is clamped to be above any obstacle
    /// box whose X range overlaps the routing corridor. When src or dst IS a
    /// box, we exit/enter from top-center (going up / coming down) rather than
    /// mid-right / mid-left, so the arrow never visually sits inside the box.
    /// </summary>
    private static Vector2[] BuildRoutedPath(Rect2 src, Rect2 dst,
        bool srcIsBox, bool dstIsBox, List<Rect2> obstacleRects, Rect2? containerBox,
        int slotIdx, out Vector2 tip)
    {
        const float HeadCover = ArrowHead - 6f;

        // Same-column case: vertical arrow.
        if (Mathf.IsEqualApprox(src.Position.X, dst.Position.X))
        {
            float midX = src.Position.X + src.Size.X / 2f;
            bool srcAbove = src.Position.Y < dst.Position.Y;
            float sY = srcAbove ? src.Position.Y + src.Size.Y : src.Position.Y;
            float dY = srcAbove ? dst.Position.Y : dst.Position.Y + dst.Size.Y;
            float dirY = srcAbove ? 1f : -1f;
            // Round 9 round 31: clamp pullback so dShort never overshoots
            // past s0 (which would invert the line direction). Allow up to
            // 40% of the gap, capped at HeadCover.
            float gapY = Mathf.Abs(dY - sY);
            float pullback = Mathf.Min(HeadCover, gapY * 0.4f);
            var s0 = new Vector2(midX, sY);
            var dShort0 = new Vector2(midX, dY - dirY * pullback);
            tip = new Vector2(midX, dY);
            return new[] { s0, dShort0 };
        }

        bool isForward = dst.Position.X > src.Position.X;

        if (isForward)
        {
            // Round 9 round 31: always use right-mid / left-mid for forward
            // edges, even when src or dst is a box. Forward edges naturally
            // enter box from its left-front side which is the correct "front"
            // for incoming. Elevation is only required when the direct path
            // would cross an obstacle.
            var sF = new Vector2(src.Position.X + src.Size.X, src.Position.Y + src.Size.Y / 2f);
            var dF = new Vector2(dst.Position.X, dst.Position.Y + dst.Size.Y / 2f);

            // Round 9 round 31: precise crossesObstacle check. Compute the
            // would-be 3-segment manhattan path and check whether any of its
            // actual segments intersect any obstacle rect (excluding src,
            // dst, and the mini-src's containing box). Previously used a
            // midY heuristic that was too pessimistic (e.g. mini ENRAGE
            // (Y=186) → std OFF (Y=145) midY=165 falls inside std ENRAGE
            // [105,185], but the real H@186 / V@midX / H@145 segments all
            // miss std ENRAGE).
            float midXForCheck = dF.X - HeadCover - PostBendVisible;
            if (midXForCheck < sF.X + 8f) midXForCheck = sF.X + 8f;
            bool crossesObstacle = false;
            foreach (var b in obstacleRects)
            {
                if (RectEquals(b, src) || RectEquals(b, dst)) continue;
                if (containerBox.HasValue && RectEquals(b, containerBox.Value)) continue;
                float bL = b.Position.X;
                float bR = b.Position.X + b.Size.X;
                float bT = b.Position.Y;
                float bB = b.Position.Y + b.Size.Y;
                // Segment 1: horizontal at sF.Y from sF.X to midXForCheck.
                if (sF.Y > bT && sF.Y < bB
                    && sF.X < bR && midXForCheck > bL)
                { crossesObstacle = true; break; }
                // Segment 2: vertical at midXForCheck from sF.Y to dF.Y.
                if (midXForCheck > bL && midXForCheck < bR)
                {
                    float vT = Mathf.Min(sF.Y, dF.Y);
                    float vB = Mathf.Max(sF.Y, dF.Y);
                    if (vT < bB && vB > bT)
                    { crossesObstacle = true; break; }
                }
                // Segment 3: horizontal at dF.Y from midXForCheck to dF.X.
                if (dF.Y > bT && dF.Y < bB
                    && midXForCheck < bR && dF.X > bL)
                { crossesObstacle = true; break; }
            }

            if (!crossesObstacle)
            {
                if (Mathf.IsEqualApprox(sF.Y, dF.Y))
                {
                    var dShort = new Vector2(dF.X - HeadCover, dF.Y);
                    tip = dF;
                    return new[] { sF, dShort };
                }
                float midX = dF.X - HeadCover - PostBendVisible;
                if (midX < sF.X + 8f) midX = sF.X + 8f;
                var p1 = new Vector2(midX, sF.Y);
                var p2 = new Vector2(midX, dF.Y);
                var dShort3 = new Vector2(dF.X - HeadCover, dF.Y);
                tip = dF;
                return new[] { sF, p1, p2, dShort3 };
            }

            // Elevated forward routing (crosses obstacle): route over the top
            // of any obstacle like a back-edge does. Exit right-mid → up to
            // channel → across above obstacles → down into dst left-mid.
            // Round 9 round 31: only consider obstacles in the SAME vertical
            // row band as src/dst — otherwise multi-row layouts route their
            // intra-row back-edges all the way to the panel top.
            float topCeilingF = Mathf.Min(src.Position.Y, dst.Position.Y);
            float corridorLF = Mathf.Min(sF.X, dF.X) - HBend;
            float corridorRF = Mathf.Max(sF.X, dF.X) + HBend;
            float bandTopF = Mathf.Min(src.Position.Y, dst.Position.Y) - 30f;
            float bandBotF = Mathf.Max(src.Position.Y + src.Size.Y, dst.Position.Y + dst.Size.Y) + 30f;
            foreach (var b in obstacleRects)
            {
                float bL = b.Position.X;
                float bR = b.Position.X + b.Size.X;
                if (bR < corridorLF || bL > corridorRF) continue;
                if (b.Position.Y + b.Size.Y < bandTopF) continue;
                if (b.Position.Y > bandBotF) continue;
                if (b.Position.Y < topCeilingF) topCeilingF = b.Position.Y;
            }
            float channelYF = topCeilingF - ChannelGap - slotIdx * SlotChannelStep;
            if (channelYF < 4f) channelYF = 4f;
            float bendDF = HBend + slotIdx * SlotBendStep;

            var p1F = new Vector2(sF.X + bendDF, sF.Y);
            var p2F = new Vector2(sF.X + bendDF, channelYF);
            var p3F = new Vector2(dF.X - bendDF, channelYF);
            var p4F = new Vector2(dF.X - bendDF, dF.Y);
            var dShortF = new Vector2(dF.X - HeadCover, dF.Y);
            tip = dF;
            return new[] { sF, p1F, p2F, p3F, p4F, dShortF };
        }

        // ── Back-edge ──
        // Exit point: top-center if src is a box, else right-mid.
        var s = srcIsBox
            ? new Vector2(src.Position.X + src.Size.X / 2f, src.Position.Y)
            : new Vector2(src.Position.X + src.Size.X, src.Position.Y + src.Size.Y / 2f);
        // Entry point (and arrow tip): top-center if dst is a box, else left-mid.
        var d = dstIsBox
            ? new Vector2(dst.Position.X + dst.Size.X / 2f, dst.Position.Y)
            : new Vector2(dst.Position.X, dst.Position.Y + dst.Size.Y / 2f);

        // Channel Y: above both endpoints' TOPS, and above every obstacle box
        // whose X range overlaps the routing corridor [min, max] of the edge.
        // Round 9 round 31: only consider obstacles in the SAME vertical row
        // band as src/dst, so intra-row back-edges in multi-row layouts don't
        // route over completely unrelated rows above.
        float topCeiling = Mathf.Min(src.Position.Y, dst.Position.Y);
        float corridorL = Mathf.Min(s.X, d.X) - HBend;
        float corridorR = Mathf.Max(s.X, d.X) + HBend;
        float bandTop = Mathf.Min(src.Position.Y, dst.Position.Y) - 30f;
        float bandBot = Mathf.Max(src.Position.Y + src.Size.Y, dst.Position.Y + dst.Size.Y) + 30f;
        foreach (var b in obstacleRects)
        {
            float bL = b.Position.X;
            float bR = b.Position.X + b.Size.X;
            if (bR < corridorL || bL > corridorR) continue;
            if (b.Position.Y + b.Size.Y < bandTop) continue;
            if (b.Position.Y > bandBot) continue;
            if (b.Position.Y < topCeiling) topCeiling = b.Position.Y;
        }
        // Round 9 round 31: slotIdx shifts channelY up and bendX right per
        // slot so overlapping back-edges (e.g. two mini-src edges from the
        // same container box) don't share the same vertical/horizontal.
        float channelY = topCeiling - ChannelGap - slotIdx * SlotChannelStep;
        if (channelY < 4f) channelY = 4f;
        float bendDX = HBend + slotIdx * SlotBendStep;

        // Build path: s → (up-or-right-then-up) → horizontal across → (down-or-down-then-right) → tip.
        var pts = new List<Vector2>();
        pts.Add(s);
        if (srcIsBox)
        {
            pts.Add(new Vector2(s.X, channelY));
        }
        else
        {
            pts.Add(new Vector2(s.X + bendDX, s.Y));
            pts.Add(new Vector2(s.X + bendDX, channelY));
        }
        if (dstIsBox)
        {
            pts.Add(new Vector2(d.X, channelY));
            var dShortBox = new Vector2(d.X, d.Y - HeadCover);
            pts.Add(dShortBox);
            tip = d;
        }
        else
        {
            pts.Add(new Vector2(d.X - bendDX, channelY));
            pts.Add(new Vector2(d.X - bendDX, d.Y));
            var dShort = new Vector2(d.X - HeadCover, d.Y);
            pts.Add(dShort);
            tip = d;
        }
        return pts.ToArray();
    }

    /// <summary>
    /// Build a secondary merge path: from srcRect's right-mid to the nearest
    /// point on the trunk polyline, using an orthogonal 3-segment routing:
    ///   s → (s.X + HBend, s.Y) → (s.X + HBend, targetPt.Y) → targetPt
    /// This matches the "向右、向上、向左汇入" pattern the user described.
    /// Degenerate cases collapse to 2 segments.
    /// </summary>
    private static Vector2[] BuildSecondaryMergePath(Rect2 srcRect, Vector2[] trunkPath)
    {
        var s = new Vector2(srcRect.Position.X + srcRect.Size.X,
                            srcRect.Position.Y + srcRect.Size.Y / 2f);
        var target = ClosestPointOnPolyline(trunkPath, s);

        // Always exit to the RIGHT of the source cell using the HBend stub so
        // the arrow icon's forward direction remains visually consistent.
        float bendX = s.X + HBend;
        if (bendX > target.X - 4f && target.X > s.X)
        {
            // Target is close to s on the right — simple 2-segment L.
            var corner = new Vector2(target.X, s.Y);
            if (Mathf.IsEqualApprox(s.Y, target.Y)) return new[] { s, target };
            return new[] { s, corner, target };
        }
        var p1 = new Vector2(bendX, s.Y);
        var p2 = new Vector2(bendX, target.Y);
        if (Mathf.IsEqualApprox(p2.X, target.X)) return new[] { s, p1, target };
        return new[] { s, p1, p2, target };
    }

    private static Vector2 ClosestPointOnPolyline(Vector2[] poly, Vector2 pt)
    {
        float best = float.MaxValue;
        Vector2 bestPt = poly[0];
        for (int i = 0; i + 1 < poly.Length; i++)
        {
            var a = poly[i];
            var b = poly[i + 1];
            var ab = b - a;
            float lenSq = ab.LengthSquared();
            if (lenSq < 1e-6f) continue;
            float t = (pt - a).Dot(ab) / lenSq;
            if (t < 0f) t = 0f;
            if (t > 1f) t = 1f;
            var p = a + ab * t;
            float d = (p - pt).LengthSquared();
            if (d < best) { best = d; bestPt = p; }
        }
        return bestPt;
    }

    /// <summary>
    /// Draw a self-loop on the right side of a cell as a "[" shape:
    /// exit top-right, walk right, walk down past the bottom of the cell,
    /// walk left back into the cell at bottom-right with an arrow head.
    /// Used for boxes whose hidden branches all return to themselves
    /// (the canonical "cycle indefinitely" indicator).
    /// </summary>
    private void AddSelfLoop(Rect2 r, Color color)
    {
        // Round 9 round 21: bracket size increased so the down/left bend has
        // a clearly-visible horizontal segment (not just a thin tab).
        // Round 9 round 27: pull the last segment back by HeadCover so the
        // triangle base sits at the line endpoint (same trick as AddRoutedArrow).
        // Without this, the triangle's narrow tip is AT the line's last point,
        // and the line shaft protrudes from the triangle on the sides near
        // the tip because the triangle is narrower than the line stroke there.
        const float HeadCover = ArrowHead - 6f;
        float xRight = r.Position.X + r.Size.X;
        float midY   = r.Position.Y + r.Size.Y / 2f;
        float yTop   = midY - 26f;
        float yBot   = midY + 26f;
        float xOut   = xRight + 36f;
        var p0 = new Vector2(xRight, yTop);
        var p1 = new Vector2(xOut,   yTop);
        var p2 = new Vector2(xOut,   yBot);
        var p3 = new Vector2(xRight, yBot);
        // Last segment direction is p2 → p3 (going LEFT, negative X).
        // Pull the line endpoint back by HeadCover in the OPPOSITE direction
        // (toward p2, positive X).
        var p3Short = new Vector2(xRight + HeadCover, yBot);
        AddLine(new[] { p0, p1, p2, p3Short }, color);
        AddArrowHead(p3Short, p3, color);
    }

    private void AddLine(Vector2[] points, Color color)
    {
        var line = new Line2D
        {
            DefaultColor = color,
            Width = ArrowStroke,
            Antialiased = true,
            ZIndex = 10,
            JointMode = Line2D.LineJointMode.Sharp,
            BeginCapMode = Line2D.LineCapMode.None,
            EndCapMode = Line2D.LineCapMode.None,
        };
        foreach (var p in points) line.AddPoint(p);
        AddChild(line);
    }

    private void AddArrowHead(Vector2 from, Vector2 to, Color color)
    {
        // `from` is the line's last (pulled-back) point. `to` is the original
        // destination. We place the triangle so its base sits ON `from` (the
        // line's last point — fully covering the line cap) and the tip extends
        // forward to `to` plus a small overshoot. This guarantees the line
        // shaft is never visible inside or beyond the triangle area.
        var dir = (to - from).Normalized();
        if (dir == Vector2.Zero) return;
        var perp = new Vector2(-dir.Y, dir.X);
        var baseCenter = from;
        var tip = to + dir * 4f;  // small overshoot past the cell edge
        var p1 = baseCenter + perp * (ArrowHead * 0.55f);
        var p2 = baseCenter - perp * (ArrowHead * 0.55f);
        var head = new Polygon2D
        {
            Color = color,
            Polygon = new[] { tip, p1, p2 },
            ZIndex = 10,
        };
        AddChild(head);
    }
}
