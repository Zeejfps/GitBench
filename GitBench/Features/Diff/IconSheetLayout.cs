using ZGF.Geometry;

namespace GitBench.Features.Diff;

/// <summary>One frame's slot in an icon sheet: where its picture goes and where its size label sits.</summary>
internal readonly record struct IconSheetCell(int FrameIndex, RectF Image, RectF Label);

/// <summary>
/// Arranges an icon container's frames as a contact sheet — every entry drawn at its own pixel
/// size, wrapped into rows and shrunk uniformly only when the sheet cannot otherwise fit the space
/// it is given. The height is computable without a canvas, so a host that reserves the slot (the
/// stacked review list) and the surface that draws into it agree on the size.
/// </summary>
internal static class IconSheetLayout
{
    /// <summary>Room under each picture for its size label.</summary>
    internal const float LabelHeight = 14f;

    private const float LabelGap = 4f;
    private const float CellGap = 12f;
    private const float RowGap = 16f;
    // Keeps a 16px entry's cell wide enough for "16 × 16" underneath it.
    private const float MinCellWidth = 64f;
    private const float MinScale = 0.05f;
    private const int FitSteps = 10;

    /// <summary>The height the sheet takes in <paramref name="width"/>, never more than it must.</summary>
    public static float Measure(IReadOnlyList<ImageFrame> frames, float width, float maxHeight)
        => Walk(frames, width, FitScale(frames, width, maxHeight), null, default, 0f);

    /// <summary>Fills <paramref name="cells"/> with the frames placed inside <paramref name="area"/>.</summary>
    public static void Arrange(IReadOnlyList<ImageFrame> frames, RectF area, List<IconSheetCell> cells)
    {
        cells.Clear();
        var scale = FitScale(frames, area.Width, area.Height);
        var height = Walk(frames, area.Width, scale, null, default, 0f);
        Walk(frames, area.Width, scale, cells, area, height);
    }

    // The largest scale whose sheet fits the height. Height falls as the scale does, so a bisection
    // lands on it; the wrapping means it does not fall smoothly, which rules out solving directly.
    private static float FitScale(IReadOnlyList<ImageFrame> frames, float width, float height)
    {
        if (width <= 0f || height <= 0f) return MinScale;
        if (Walk(frames, width, 1f, null, default, 0f) <= height) return 1f;

        var lo = MinScale;
        var hi = 1f;
        for (var i = 0; i < FitSteps; i++)
        {
            var mid = (lo + hi) * 0.5f;
            if (Walk(frames, width, mid, null, default, 0f) <= height) lo = mid;
            else hi = mid;
        }
        return lo;
    }

    // Walks the wrapped rows at a fixed scale and returns their total height. With no cell list it
    // only measures; with one it also places, which needs the total height up front to centre the
    // block — hence the two passes over the same deterministic walk rather than a row buffer.
    private static float Walk(
        IReadOnlyList<ImageFrame> frames,
        float width,
        float scale,
        List<IconSheetCell>? cells,
        RectF area,
        float totalHeight)
    {
        var top = cells == null ? 0f : area.Top - (area.Height - totalHeight) * 0.5f;
        var total = 0f;
        var start = 0;

        while (start < frames.Count)
        {
            var rowWidth = 0f;
            var rowImageHeight = 0f;
            var end = start;
            while (end < frames.Count)
            {
                var size = FrameSize(frames[end], width, scale);
                var cellWidth = MathF.Max(size.X, MinCellWidth);
                var extended = end == start ? cellWidth : rowWidth + CellGap + cellWidth;
                if (end > start && extended > width) break;
                rowWidth = extended;
                rowImageHeight = MathF.Max(rowImageHeight, size.Y);
                end++;
            }

            if (cells != null) PlaceRow(frames, start, end, width, scale, rowWidth, rowImageHeight, top, area, cells);

            var rowHeight = rowImageHeight + LabelGap + LabelHeight;
            total += rowHeight + (end < frames.Count ? RowGap : 0f);
            top -= rowHeight + RowGap;
            start = end;
        }

        return total;
    }

    // Pictures in a row sit on a shared baseline so their labels line up; a smaller entry hangs
    // below the taller one beside it rather than floating in the middle of its cell.
    private static void PlaceRow(
        IReadOnlyList<ImageFrame> frames,
        int start,
        int end,
        float width,
        float scale,
        float rowWidth,
        float rowImageHeight,
        float top,
        RectF area,
        List<IconSheetCell> cells)
    {
        var x = area.Left + (area.Width - rowWidth) * 0.5f;
        var baseline = top - rowImageHeight;

        for (var i = start; i < end; i++)
        {
            var size = FrameSize(frames[i], width, scale);
            var cellWidth = MathF.Max(size.X, MinCellWidth);
            cells.Add(new IconSheetCell(
                i,
                Round(new RectF(x + (cellWidth - size.X) * 0.5f, baseline, size.X, size.Y)),
                Round(new RectF(x, baseline - LabelGap - LabelHeight, cellWidth, LabelHeight))));
            x += cellWidth + CellGap;
        }
    }

    // Never magnified past its own pixels, and never wider than the sheet — a lone 256px entry in a
    // narrow pane still has to fit.
    private static PointF FrameSize(ImageFrame frame, float width, float scale)
    {
        var fit = MathF.Min(scale, width / frame.Width);
        return new PointF(MathF.Max(1f, frame.Width * fit), MathF.Max(1f, frame.Height * fit));
    }

    private static RectF Round(RectF r) => new(
        MathF.Round(r.Left), MathF.Round(r.Bottom), MathF.Round(r.Width), MathF.Round(r.Height));
}
