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
    // How far each lane-shift curve's control points are pushed past the vertical midpoint (0.5 =
    // midpoint). Higher values keep the curve vertical longer at each end and sharpen the central
    // crossing, reading as a more pronounced "S".
    private const float CurveTension = 0.85f;

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
        foreach (var l in node.PassThroughLanes) if (l > maxLane) maxLane = l;
        foreach (var l in node.IncomingLanes) if (l > maxLane) maxLane = l;
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

        // A lane-shift edge is one S-curve spanning the whole gap between two dots, drawn by the dot
        // it springs from (a divergence descends from the row above; a convergence rises from the row
        // below). So this row must skip its own straight stub on a lane a neighbour's curve already
        // owns, or the stub and the curve would overlap into a forked double line:
        //   top half covered    -> the row above branched a NEW lane down into this one;
        //   bottom half covered -> the row below merges this lane up into its dot.
        bool TopCovered(int lane) => prevNode is not null && IsNewLaneShift(prevNode, lane);
        bool BottomCovered(int lane) => nextNode is not null && Contains(nextNode.IncomingLanes, lane);

        // Pass-through verticals (lanes with no interaction at this row) stay solid — they belong to
        // whatever lane is passing, not to this (possibly dashed) commit. A covered half is dropped so
        // the neighbour's curve owns it.
        foreach (var ptLane in node.PassThroughLanes)
        {
            var x = Mx(LaneCenterX(graphStartX, ptLane, laneCount));
            var yLow = BottomCovered(ptLane) ? rowCenterY : rowBottom;
            var yHigh = TopCovered(ptLane) ? rowCenterY : rowTop;
            if (yHigh > yLow)
                DrawSegment(c, x, yLow, x, yHigh, LaneColor(ptLane), z);
        }

        // Top half of commit's own lane — unless it's the tail of a divergence curve descending into
        // this dot, which already covers it.
        if (node.HasIncomingAtCommitLane && !TopCovered(node.Lane))
        {
            DrawSegment(c, commitCx, rowCenterY, commitCx, rowTop, commitColor, z, dashed: dashed);
        }

        // Convergence: lanes merging into this dot from above. Each is a full-gap S rising straight
        // from the source dot one row up into this circle, blending the source lane's color into this
        // commit's. The source row drops its matching bottom-half stub (see BottomCovered there).
        foreach (var inLane in node.IncomingLanes)
        {
            var inCx = Mx(LaneCenterX(graphStartX, inLane, laneCount));
            DrawCurve(c, inCx, rowCenterY + rowHeight, commitCx, rowCenterY, LaneColor(inLane), z, commitColor, dashed);
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
                // Merge into a lane that already runs below this row — the one edge that starts and
                // ends inside a single row, so it swoops: a horizontal run out of the dot's center
                // bending down into the existing vertical.
                DrawSwoop(c, commitCx, rowCenterY, pCx, rowBottom, commitColor, z, pColor, dashed);
            }
            else
            {
                // Divergence onto a freshly branched lane: a full-gap S running straight from this dot
                // to the next dot on that lane, so a branch leaves its parent circle with no straight
                // stub. The landing row drops its matching top-half stub (see TopCovered there).
                DrawCurve(c, commitCx, rowCenterY, pCx, rowCenterY - rowHeight, commitColor, z, pColor, dashed);
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

    private static bool Contains(IReadOnlyList<int> lanes, int lane)
    {
        for (var i = 0; i < lanes.Count; i++)
            if (lanes[i] == lane)
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

    // A smooth S between the two endpoints with vertical tangents at both, so it lines up with the
    // vertical lanes it joins. The control points sit on each endpoint's vertical, pushed past the
    // midpoint by CurveTension so the curve hugs the lane longer at each end and crosses sharply in
    // the middle — a more pronounced "S" than midpoint controls give.
    private static void DrawCurve(ICanvas c, float x0, float y0, float x1, float y1, uint color, int z,
        uint? gradientEnd = null, bool dashed = false)
    {
        var dy = y1 - y0;
        c.DrawCubicBezier(new DrawCubicBezierInputs
        {
            Start = new PointF(x0, y0),
            Control1 = new PointF(x0, y0 + dy * CurveTension),
            Control2 = new PointF(x1, y1 - dy * CurveTension),
            End = new PointF(x1, y1),
            Thickness = EdgeThickness,
            Color = color,
            ZIndex = z,
            GradientEndColor = gradientEnd,
            DashLength = dashed ? DashLength : 0f,
            GapLength = dashed ? GapLength : 0f,
        });
    }

    // A "swoop" (sideways J) for the one edge drawn entirely inside a single row: a dead-straight
    // horizontal out of the dot's center ending in one quarter-turn that lands tangent on the lane
    // it joins. The turn's radius is the full height available (the half-row), not a tight corner —
    // a small bend hides on top of the lane's running vertical, while this one sweeps out beside it
    // where it can be seen. The color blends across the horizontal run and the turn carries the
    // lane's color.
    private static void DrawSwoop(ICanvas c, float x0, float y0, float x1, float y1, uint color, int z,
        uint? gradientEnd = null, bool dashed = false)
    {
        var sx = Math.Sign(x0 - x1);
        var sy = Math.Sign(y1 - y0);
        var r = Math.Min(Math.Abs(x1 - x0), Math.Abs(y1 - y0));
        var laneColor = gradientEnd ?? color;
        if (Math.Abs(x1 - x0) > r)
            DrawSegment(c, x0, y0, x1 + sx * r, y0, color, z, gradientEnd, dashed);
        // Quarter-turn approximating a circular arc (control points k*r along the tangents).
        const float k = 0.5523f;
        c.DrawCubicBezier(new DrawCubicBezierInputs
        {
            Start = new PointF(x1 + sx * r, y0),
            Control1 = new PointF(x1 + sx * r * (1 - k), y0),
            Control2 = new PointF(x1, y0 + sy * r * (1 - k)),
            End = new PointF(x1, y0 + sy * r),
            Thickness = EdgeThickness,
            Color = laneColor,
            ZIndex = z,
            DashLength = dashed ? DashLength : 0f,
            GapLength = dashed ? GapLength : 0f,
        });
        if (Math.Abs(y1 - y0) > r)
            DrawSegment(c, x1, y0 + sy * r, x1, y1, laneColor, z, dashed: dashed);
    }
}
