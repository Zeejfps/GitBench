using GitBench.Theming;
using ZGF.Geometry;
using ZGF.Gui;

namespace GitBench.Features.Commits;

/// <summary>
/// Stateless renderer for the lane/edge/dot commit graph that occupies the left column of
/// <see cref="CommitsView"/>. Owns all lane geometry; given a
/// <see cref="CommitNode"/> and the graph's left edge it draws one row's cell. Pure draw:
/// every output is a function of its arguments, so it carries no per-view state.
/// </summary>
internal static class CommitGraphRenderer
{
    public const float PaddingLeft = 12f;
    public const float PaddingRight = 8f;

    private const float LaneWidth = 16f;
    // The graph column is sized to hold this many lanes at full LaneWidth. A snapshot with more
    // concurrent lanes keeps the same column width but compresses the per-lane spacing (see
    // LaneSpacing) so every lane still lands on a distinct x — rather than clamping the overflow
    // lanes onto the final column, which stacked dots on top of each other and dropped the
    // zero-width horizontal connectors between them.
    private const int MaxColumnLanes = 12;
    private const float DotRadius = 5f;
    private const float MinDotRadius = 1.5f;
    private const float EdgeThickness = 2f;
    private const float DashLength = 4f;
    private const float GapLength = 3f;
    private const float RingThickness = 1.75f;
    // Control-point distance, as a fraction of the radius, at which a cubic bezier approximates a
    // quarter circle.
    private const float ArcK = 0.5523f;

    private static uint LaneColor(int lane) => CategoricalPalette.Lane(lane);

    // Horizontal distance between adjacent lane centers. Up to MaxColumnLanes the column grows at
    // full LaneWidth; beyond that the same fixed column width is shared across every lane so they
    // compress to fit rather than overlapping on the last column.
    private static float LaneSpacing(int laneCount)
        => laneCount <= MaxColumnLanes ? LaneWidth : MaxColumnLanes * LaneWidth / laneCount;

    /// <summary>Width of the graph column for a snapshot spanning <paramref name="laneCount"/> lanes.</summary>
    public static float ColumnWidth(int laneCount)
    {
        var lanes = Math.Min(Math.Max(laneCount, 1), MaxColumnLanes);
        return PaddingLeft + lanes * LaneWidth + PaddingRight;
    }

    /// <summary>X where row content (badges/summary) can begin, just right of the widest
    /// lane touched by this row's dot, incoming, pass-through and outgoing edges.</summary>
    public static float SummaryStartX(float graphStartX, CommitNode node, int laneCount)
    {
        var maxLane = node.Lane;
        foreach (var l in node.PassThroughLanes) if (l.Lane > maxLane) maxLane = l.Lane;
        foreach (var l in node.IncomingLanes) if (l.Lane > maxLane) maxLane = l.Lane;
        foreach (var p in node.InWalkParentLanes) if (p.Lane > maxLane) maxLane = p.Lane;
        return LaneCenterX(graphStartX, maxLane, laneCount) + DotRadius + PaddingRight;
    }

    public static void DrawCell(ICanvas c, CommitNode node, CommitNode? prevNode, CommitNode? nextNode, float graphStartX, float rowBottom, float rowHeight, int laneCount, int z, uint rowBackground, bool isRtl = false, float mirrorBound = 0f)
    {
        // Under RTL the graph mirrors with the rest of the row: every lane x reflects within the
        // row, so lanes run right-to-left and edges/dots follow. All x's flow from LaneCenterX, so
        // reflecting its four results here mirrors the whole cell — segments/curves/dots are untouched.
        float Mx(float x) => isRtl ? mirrorBound - x : x;

        var rowTop = rowBottom + rowHeight;
        var rowCenterY = rowBottom + rowHeight * 0.5f;
        var commitColor = LaneColor(node.Lane);
        var commitCx = Mx(LaneCenterX(graphStartX, node.Lane, laneCount));
        // Stash and remote-only commits sit outside local mainline history; dash their
        // edges so they read as auxiliary. Stash also gets a hollow dot.
        var stash = IsStash(node);
        var dashed = stash || node.RemoteOnly;

        // A lane-shift edge is one curve spanning the whole gap between two dots, drawn by the dot
        // it springs from (a divergence falls from the row above as an S; a convergence swoops up
        // from the row below). So this row must skip its own straight stub on a lane a neighbour's
        // curve already owns, or the stub and the curve would overlap into a forked double line:
        //   top half covered    -> the row above branched a NEW lane down into this one;
        //   bottom half covered -> the row below merges this lane up into its dot.
        bool TopCovered(int lane) => prevNode is not null && IsNewLaneShift(prevNode, lane);
        bool BottomCovered(int lane) => nextNode is not null && Contains(nextNode.IncomingLanes, lane);

        // Pass-through verticals (lanes with no interaction at this row) belong to whatever edge is
        // passing, not to this commit — they dash by that edge's own stash/remote-only flag rather
        // than this row's. A covered half is dropped so the neighbour's curve owns it.
        foreach (var pt in node.PassThroughLanes)
        {
            var x = Mx(LaneCenterX(graphStartX, pt.Lane, laneCount));
            var yLow = BottomCovered(pt.Lane) ? rowCenterY : rowBottom;
            var yHigh = TopCovered(pt.Lane) ? rowCenterY : rowTop;
            if (yHigh > yLow)
                DrawSegment(c, x, yLow, x, yHigh, LaneColor(pt.Lane), z, dashed: pt.Dashed);
        }

        // Top half of commit's own lane — unless it's the tail of a divergence curve descending into
        // this dot, which already covers it. It belongs to the edge arriving from above, so it
        // dashes by that edge's flag rather than this commit's.
        if (node.HasIncomingAtCommitLane && !TopCovered(node.Lane))
        {
            DrawSegment(c, commitCx, rowCenterY, commitCx, rowTop, commitColor, z, dashed: node.IncomingAtCommitLaneDashed);
        }

        // Convergence: lanes whose history forks off this dot — reading chronologically, each begins
        // here. A level run out of this circle's center swoops up onto the lane and runs straight to
        // the child dot one row up, blending this commit's color into the lane's. The edge belongs
        // to the lane forking off, so it dashes by that lane's flag rather than this commit's. The
        // source row drops its matching bottom-half stub (see BottomCovered there).
        foreach (var inLane in node.IncomingLanes)
        {
            var inCx = Mx(LaneCenterX(graphStartX, inLane.Lane, laneCount));
            DrawSwoop(c, commitCx, rowCenterY, inCx, rowCenterY + rowHeight, rowHeight * 0.5f, commitColor, z, LaneColor(inLane.Lane), inLane.Dashed);
        }

        // Outgoing edges to parents (continuation + branches).
        foreach (var pl in node.InWalkParentLanes)
        {
            var pCx = Mx(LaneCenterX(graphStartX, pl.Lane, laneCount));
            var pColor = LaneColor(pl.Lane);
            if (pl.Lane == node.Lane)
            {
                // Straight continuation down the commit's own lane — unless the row below pulls this
                // lane up into its dot, whose convergence curve then owns the bottom half.
                if (!BottomCovered(node.Lane))
                    DrawSegment(c, commitCx, rowBottom, commitCx, rowCenterY, commitColor, z, dashed: dashed);
            }
            else if (Contains(node.PassThroughLanes, pl.Lane))
            {
                // Merge into a lane that already runs below this row: swoop into the existing
                // vertical, landing tangent on it at the row bottom.
                DrawSwoop(c, commitCx, rowCenterY, pCx, rowBottom, rowHeight * 0.5f, commitColor, z, pColor, dashed);
            }
            else
            {
                // Divergence onto a freshly branched lane — the branch tip merging back into this
                // commit. A full-gap S from this circle down to the tip's dot, vertical-tangent at
                // both ends so the tip reads as a continuation flowing into the main line. The
                // landing row drops its matching top-half stub (see TopCovered there).
                DrawCurve(c, commitCx, rowCenterY, pCx, rowCenterY - rowHeight, rowHeight * 0.5f, commitColor, z, pColor, dashed);
            }
        }

        // The dot — shrinks with the lane spacing so compressed (dense) graphs don't stack dots.
        // Stash and remote-only commits draw as a hollow ring to set them apart from local history.
        var dotRadius = Math.Min(DotRadius, Math.Max(MinDotRadius, LaneSpacing(laneCount) * 0.5f - 1f));
        var dotCenter = new PointF(commitCx, rowCenterY);
        if (dashed)
        {
            // Knock out the edge inside the ring with the row background so the hollow
            // center reads cleanly instead of showing the connector through it.
            c.DrawCircle(new DrawCircleInputs
            {
                Center = dotCenter,
                Radius = dotRadius,
                Color = rowBackground,
                ZIndex = z + 1,
            });
            c.DrawCircle(new DrawCircleInputs
            {
                Center = dotCenter,
                Radius = dotRadius,
                Color = commitColor,
                ZIndex = z + 1,
                Thickness = RingThickness,
            });
        }
        else
        {
            c.DrawCircle(new DrawCircleInputs
            {
                Center = dotCenter,
                Radius = dotRadius,
                Color = commitColor,
                ZIndex = z + 1,
            });
        }
    }

    internal static float LaneCenterX(float graphStartX, int lane, int laneCount)
    {
        var spacing = LaneSpacing(laneCount);
        return graphStartX + lane * spacing + spacing * 0.5f;
    }

    private static bool IsStash(CommitNode node)
    {
        foreach (var r in node.Refs)
            if (r.Kind == RefKind.Stash)
                return true;
        return false;
    }

    // True when <paramref name="above"/> branches a parent onto <paramref name="lane"/> as a freshly
    // allocated lane — neither its own lane nor an already-running pass-through. That parent edge is
    // the divergence S-curve descending into the row below, so the row below must skip its top stub
    // there. Matches the divergence branch in the outgoing-edge loop above.
    private static bool IsNewLaneShift(CommitNode above, int lane)
    {
        if (lane == above.Lane || Contains(above.PassThroughLanes, lane))
            return false;
        foreach (var pl in above.InWalkParentLanes)
            if (pl.Lane == lane)
                return true;
        return false;
    }

    private static bool Contains(IReadOnlyList<GraphLane> lanes, int lane)
    {
        for (var i = 0; i < lanes.Count; i++)
            if (lanes[i].Lane == lane)
                return true;
        return false;
    }

    private static void DrawSegment(ICanvas c, float x0, float y0, float x1, float y1, uint color, int z,
        uint? gradientEnd = null, bool dashed = false)
    {
        c.DrawLine(new DrawLineInputs
        {
            Start = new PointF(x0, y0),
            End = new PointF(x1, y1),
            Thickness = EdgeThickness,
            Color = color,
            ZIndex = z,
            GradientEndColor = gradientEnd,
            DashLength = dashed ? DashLength : 0f,
            GapLength = dashed ? GapLength : 0f,
        });
    }

    // The S between two dots on different lanes, built from the same quarter-turns as DrawSwoop so
    // every bend in the graph shares one radius: a straight drop out of each dot, a turn off each
    // vertical, and a level run between them. Vertical tangents at both ends keep a merge tip
    // reading as a continuation of the lane it flows into. The color blends across the middle —
    // the level run when there is one, the turns when the lanes sit too close for one.
    private static void DrawCurve(ICanvas c, float x0, float y0, float x1, float y1, float maxR, uint color, int z,
        uint? gradientEnd = null, bool dashed = false)
    {
        var sx = Math.Sign(x1 - x0);
        var sy = Math.Sign(y1 - y0);
        var dx = Math.Abs(x1 - x0);
        var dy = Math.Abs(y1 - y0);
        var r = Math.Min(maxR, Math.Min(dx * 0.5f, dy * 0.5f));
        var v = (dy - 2f * r) * 0.5f;
        var yMid = y0 + sy * (v + r);
        var endColor = gradientEnd ?? color;
        var hasRun = dx > 2f * r;
        var midColor = MidColor(color, endColor);
        if (v > 0f)
        {
            DrawSegment(c, x0, y0, x0, y0 + sy * v, color, z, dashed: dashed);
            DrawSegment(c, x1, y1 - sy * v, x1, y1, endColor, z, dashed: dashed);
        }
        DrawBezier(c,
            new PointF(x0, y0 + sy * v),
            new PointF(x0, y0 + sy * (v + ArcK * r)),
            new PointF(x0 + sx * r * (1 - ArcK), yMid),
            new PointF(x0 + sx * r, yMid),
            color, z, hasRun ? null : midColor, dashed);
        if (hasRun)
            DrawSegment(c, x0 + sx * r, yMid, x1 - sx * r, yMid, color, z, gradientEnd, dashed);
        DrawBezier(c,
            new PointF(x1 - sx * r, yMid),
            new PointF(x1 - sx * r * (1 - ArcK), yMid),
            new PointF(x1, yMid + sy * r * (1 - ArcK)),
            new PointF(x1, yMid + sy * r),
            hasRun ? endColor : midColor, z, hasRun ? null : endColor, dashed);
    }

    // Per-channel average of two ARGB colors.
    private static uint MidColor(uint a, uint b) => ((a ^ b) >> 1 & 0x7F7F7F7Fu) + (a & b);

    // The "swoop" (sideways J) drawn where a lane forks off a dot: a dead-straight horizontal out
    // of the dot's center ending in one quarter-turn that lands tangent on the lane it joins,
    // then a straight run along that lane to the far end. The turn's radius fills the space
    // available up to maxR (half a row) rather than staying a tight corner — a small bend hides on
    // top of the lane's running vertical, while this one sweeps out beside it where it can be seen.
    // The color blends across the horizontal run; the turn and lane run carry the lane's color.
    private static void DrawSwoop(ICanvas c, float x0, float y0, float x1, float y1, float maxR, uint color, int z,
        uint? gradientEnd = null, bool dashed = false)
    {
        var sx = Math.Sign(x0 - x1);
        var sy = Math.Sign(y1 - y0);
        var r = Math.Min(maxR, Math.Min(Math.Abs(x1 - x0), Math.Abs(y1 - y0)));
        var laneColor = gradientEnd ?? color;
        if (Math.Abs(x1 - x0) > r)
            DrawSegment(c, x0, y0, x1 + sx * r, y0, color, z, gradientEnd, dashed);
        DrawBezier(c,
            new PointF(x1 + sx * r, y0),
            new PointF(x1 + sx * r * (1 - ArcK), y0),
            new PointF(x1, y0 + sy * r * (1 - ArcK)),
            new PointF(x1, y0 + sy * r),
            laneColor, z, null, dashed);
        if (Math.Abs(y1 - y0) > r)
            DrawSegment(c, x1, y0 + sy * r, x1, y1, laneColor, z, dashed: dashed);
    }

    private static void DrawBezier(ICanvas c, PointF start, PointF control1, PointF control2, PointF end,
        uint color, int z, uint? gradientEnd, bool dashed)
    {
        c.DrawCubicBezier(new DrawCubicBezierInputs
        {
            Start = start,
            Control1 = control1,
            Control2 = control2,
            End = end,
            Thickness = EdgeThickness,
            Color = color,
            ZIndex = z,
            GradientEndColor = gradientEnd,
            DashLength = dashed ? DashLength : 0f,
            GapLength = dashed ? GapLength : 0f,
        });
    }
}
