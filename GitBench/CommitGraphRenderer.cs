using ZGF.Geometry;
using ZGF.Gui;

namespace GitGui;

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
    private const int MaxRenderedLanes = 12;
    private const float DotRadius = 5f;
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
    };

    private static uint LaneColor(int lane) => LanePalette[((lane % LanePalette.Length) + LanePalette.Length) % LanePalette.Length];

    /// <summary>Width of the graph column for a snapshot spanning <paramref name="laneCount"/> lanes.</summary>
    public static float ColumnWidth(int laneCount)
    {
        var lanes = Math.Min(Math.Max(laneCount, 1), MaxRenderedLanes);
        return PaddingLeft + lanes * LaneWidth + PaddingRight;
    }

    /// <summary>X where row content (badges/summary) can begin, just right of the widest
    /// lane touched by this row's dot, incoming, pass-through and outgoing edges.</summary>
    public static float SummaryStartX(float graphStartX, CommitNode node)
    {
        var maxLane = node.Lane;
        foreach (var l in node.PassThroughLanes) if (l > maxLane) maxLane = l;
        foreach (var l in node.IncomingLanes) if (l > maxLane) maxLane = l;
        foreach (var p in node.InWalkParentLanes) if (p.Lane > maxLane) maxLane = p.Lane;
        return LaneCenterX(graphStartX, maxLane) + DotRadius + PaddingRight;
    }

    public static void DrawCell(ICanvas c, CommitNode node, float graphStartX, float rowBottom, float rowHeight, int z)
    {
        var rowCenterY = rowBottom + rowHeight * 0.5f;
        var commitColor = LaneColor(node.Lane);
        var commitCx = LaneCenterX(graphStartX, node.Lane);

        // Pass-through verticals (lanes with no interaction at this row).
        foreach (var ptLane in node.PassThroughLanes)
        {
            DrawVertical(c, LaneCenterX(graphStartX, ptLane), rowBottom, rowHeight,
                LaneColor(ptLane), z);
        }

        // Top half of commit's own lane (only if an edge continues from above).
        if (node.HasIncomingAtCommitLane)
        {
            DrawVertical(c, commitCx, rowCenterY, rowHeight * 0.5f, commitColor, z);
        }

        // Incoming merge edges from other lanes above this commit.
        foreach (var inLane in node.IncomingLanes)
        {
            var inCx = LaneCenterX(graphStartX, inLane);
            var inColor = LaneColor(inLane);
            DrawVertical(c, inCx, rowCenterY, rowHeight * 0.5f, inColor, z);
            DrawHorizontal(c, Math.Min(inCx, commitCx), Math.Max(inCx, commitCx), rowCenterY, inColor, z);
        }

        // Outgoing edges to parents (continuation + branches).
        foreach (var pl in node.InWalkParentLanes)
        {
            var pCx = LaneCenterX(graphStartX, pl.Lane);
            var pColor = LaneColor(pl.Lane);
            if (pl.Lane == node.Lane)
            {
                DrawVertical(c, commitCx, rowBottom, rowHeight * 0.5f, commitColor, z);
            }
            else
            {
                DrawHorizontal(c, Math.Min(commitCx, pCx), Math.Max(commitCx, pCx), rowCenterY, pColor, z);
                DrawVertical(c, pCx, rowBottom, rowHeight * 0.5f, pColor, z);
            }
        }

        // The dot.
        var dotRect = new RectF(commitCx - DotRadius, rowCenterY - DotRadius, DotRadius * 2, DotRadius * 2);
        c.DrawRect(new DrawRectInputs
        {
            Position = dotRect,
            Style = new RectStyle
            {
                BackgroundColor = commitColor,
                BorderRadius = BorderRadiusStyle.All(DotRadius),
            },
            ZIndex = z + 1,
        });
    }

    private static float LaneCenterX(float graphStartX, int lane)
        => graphStartX + Math.Min(lane, MaxRenderedLanes - 1) * LaneWidth + LaneWidth * 0.5f;

    private static void DrawVertical(ICanvas c, float cx, float bottomY, float height, uint color, int z)
    {
        c.DrawRect(new DrawRectInputs
        {
            Position = new RectF(cx - EdgeThickness * 0.5f, bottomY, EdgeThickness, height),
            Style = new RectStyle { BackgroundColor = color },
            ZIndex = z,
        });
    }

    private static void DrawHorizontal(ICanvas c, float leftX, float rightX, float cy, uint color, int z)
    {
        var w = rightX - leftX;
        if (w <= 0) return;
        c.DrawRect(new DrawRectInputs
        {
            Position = new RectF(leftX, cy - EdgeThickness * 0.5f, w, EdgeThickness),
            Style = new RectStyle { BackgroundColor = color },
            ZIndex = z,
        });
    }
}
