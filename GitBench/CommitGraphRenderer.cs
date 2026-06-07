using ZGF.Geometry;
using ZGF.Gui;

namespace GitBench;

/// <summary>
/// Stateless renderer for the lane/edge/dot commit graph that occupies the left column of
/// <see cref="CommitsView"/>. Owns all lane geometry and the lane color palette; given a
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

    private static readonly uint[] LanePalette =
    {
        0xFF5865F2,
        0xFFEB459E,
        0xFF57F287,
        0xFFFEE75C,
        0xFFED4245,
        0xFF9B59B6,
        0xFFE67E22,
        0xFF1ABC9C,
        0xFF3498DB,
        0xFFE91E63,
        0xFF2ECC71,
        0xFFF1C40F,
    };

    private static uint LaneColor(int lane) => LanePalette[((lane % LanePalette.Length) + LanePalette.Length) % LanePalette.Length];

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

    public static void DrawCell(ICanvas c, CommitNode node, float graphStartX, float rowBottom, float rowHeight, int laneCount, int z)
    {
        var rowTop = rowBottom + rowHeight;
        var rowCenterY = rowBottom + rowHeight * 0.5f;
        var commitColor = LaneColor(node.Lane);
        var commitCx = LaneCenterX(graphStartX, node.Lane, laneCount);

        // Pass-through verticals (lanes with no interaction at this row).
        foreach (var ptLane in node.PassThroughLanes)
        {
            var x = LaneCenterX(graphStartX, ptLane, laneCount);
            DrawSegment(c, x, rowBottom, x, rowTop, LaneColor(ptLane), z);
        }

        // Top half of commit's own lane (only if an edge continues from above).
        if (node.HasIncomingAtCommitLane)
        {
            DrawSegment(c, commitCx, rowCenterY, commitCx, rowTop, commitColor, z);
        }

        // Incoming merge edges from other lanes above this commit: a curve that
        // leaves the lane vertically at the top boundary (so it lines up with the
        // vertical above) and bends through the elbow into the dot.
        foreach (var inLane in node.IncomingLanes)
        {
            var inCx = LaneCenterX(graphStartX, inLane, laneCount);
            DrawCurve(c, inCx, rowTop, inCx, rowCenterY, commitCx, rowCenterY, LaneColor(inLane), z);
        }

        // Outgoing edges to parents (continuation + branches).
        foreach (var pl in node.InWalkParentLanes)
        {
            var pCx = LaneCenterX(graphStartX, pl.Lane, laneCount);
            var pColor = LaneColor(pl.Lane);
            if (pl.Lane == node.Lane)
            {
                DrawSegment(c, commitCx, rowBottom, commitCx, rowCenterY, commitColor, z);
            }
            else
            {
                // Curve from the dot through the elbow down to where the parent lane
                // continues below, arriving vertically so it lines up with the vertical.
                DrawCurve(c, commitCx, rowCenterY, pCx, rowCenterY, pCx, rowBottom, pColor, z);
            }
        }

        // The dot — shrinks with the lane spacing so compressed (dense) graphs don't stack dots.
        var dotRadius = Math.Min(DotRadius, Math.Max(MinDotRadius, LaneSpacing(laneCount) * 0.5f - 1f));
        c.DrawCircle(new DrawCircleInputs
        {
            Center = new PointF(commitCx, rowCenterY),
            Radius = dotRadius,
            Color = commitColor,
            ZIndex = z + 1,
        });
    }

    internal static float LaneCenterX(float graphStartX, int lane, int laneCount)
    {
        var spacing = LaneSpacing(laneCount);
        return graphStartX + lane * spacing + spacing * 0.5f;
    }

    private static void DrawSegment(ICanvas c, float x0, float y0, float x1, float y1, uint color, int z)
    {
        c.DrawLine(new DrawLineInputs
        {
            Start = new PointF(x0, y0),
            End = new PointF(x1, y1),
            Thickness = EdgeThickness,
            Color = color,
            ZIndex = z,
        });
    }

    private static void DrawCurve(ICanvas c, float x0, float y0, float cx, float cy, float x1, float y1, uint color, int z)
    {
        c.DrawBezier(new DrawBezierInputs
        {
            Start = new PointF(x0, y0),
            Control = new PointF(cx, cy),
            End = new PointF(x1, y1),
            Thickness = EdgeThickness,
            Color = color,
            ZIndex = z,
        });
    }
}
