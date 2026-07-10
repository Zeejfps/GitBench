using System;
using GitBench.Features.LocalChanges;
using GitBench.Widgets;
using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Widgets;

namespace GitBench.Controls;

// What a tree row draws at one ancestry level. None: the ancestor's subtree already ended above, so
// nothing. Through: a vertical trunk passing straight through (an ancestor with more siblings below).
// Tee/Corner: the row's own connector to its parent — a vertical down to this row plus a horizontal
// elbow into it; Tee continues the trunk past the row (more siblings follow), Corner ends it (last child).
internal enum TreeGuide : byte
{
    None = 0,
    Through = 1,
    Tee = 2,
    Corner = 3,
}

// A row's full set of ancestry guides, one <see cref="TreeGuide"/> per level, packed two bits per level
// into a long so it travels as a cheap value (value equality lets a bound view skip redundant repaints
// and the branches' value-keyed row list reuse unchanged rows). Level 0 is the section/group header (the
// root) the row's top-level ancestor hangs off; each deeper level is one tree depth in. The deepest
// level (<see cref="Levels"/>-1) is the row's own connector, the shallower ones its ancestors' trunks.
// A row with no guides (a section header — it is the root) has <see cref="Levels"/> 0.
internal readonly struct TreeGuides : IEquatable<TreeGuides>
{
    private readonly long _mask;

    public TreeGuides(long mask, int levels)
    {
        _mask = mask;
        Levels = levels;
    }

    public int Levels { get; }

    public TreeGuide At(int level) => (TreeGuide)((_mask >> (level * 2)) & 0b11);

    public static long SetKind(long mask, int level, TreeGuide kind) =>
        (mask & ~(0b11L << (level * 2))) | ((long)kind << (level * 2));

    public bool Equals(TreeGuides other) => _mask == other._mask && Levels == other.Levels;
    public override bool Equals(object? obj) => obj is TreeGuides g && Equals(g);
    public override int GetHashCode() => HashCode.Combine(_mask, Levels);
}

// Paints a row's indent guides into a caller-supplied row rect: a vertical hairline per ancestry
// level, plus the elbow that connects the row to its parent. Column widths come from the shared
// <see cref="TreeMetrics"/>. Shared by the widget trees (via <see cref="TreeGuidesView"/>) and the
// canvas-painted file lists, which call it directly from their row painters. gapBridge is how far a
// continuing trunk overruns each open end of the row — the widget trees carry a small gap between
// rows the line must bridge; contiguous virtualized lists pass 0.
internal static class TreeGuidePainter
{
    private const float LineWidth = 1f;

    public static void Draw(ICanvas c, in RectF pos, TreeGuides guides, uint color, int z, bool rtl, float gapBridge)
    {
        var levels = guides.Levels;
        if (levels <= 0 || (color >> 24) == 0) return;

        var half = LineWidth / 2f;
        var centerY = pos.Bottom + pos.Height / 2f;
        // Where the elbow lands: the chevron column of the row's own indent (its tree depth is levels-1).
        var contentX = ColumnX(pos, levels, rtl);

        for (var level = 0; level < levels; level++)
        {
            var kind = guides.At(level);
            if (kind == TreeGuide.None) continue;

            var colX = ColumnX(pos, level, rtl);
            switch (kind)
            {
                case TreeGuide.Through:
                    // Continues both above and below: overrun both gaps.
                    Fill(c, color, z, colX - half, pos.Bottom - gapBridge, LineWidth, pos.Height + 2f * gapBridge);
                    break;
                case TreeGuide.Tee:
                    // Connects up to its parent/siblings and on down to the next sibling: overrun both.
                    Fill(c, color, z, colX - half, pos.Bottom - gapBridge, LineWidth, pos.Height + 2f * gapBridge);
                    Elbow(c, color, z, colX, contentX, centerY, half, rtl);
                    break;
                case TreeGuide.Corner:
                    // Last child: the trunk ends here, so only overrun the gap above to reach its parent.
                    Fill(c, color, z, colX - half, centerY, LineWidth, pos.Height / 2f + gapBridge);
                    Elbow(c, color, z, colX, contentX, centerY, half, rtl);
                    break;
            }
        }
    }

    // The elbow from a level's vertical into the content column, drawn from the trunk's inline-leading
    // edge to the content column so it reads as a right angle. Under RTL the content sits to the left of
    // the trunk, so the horizontal runs the other way.
    private static void Elbow(ICanvas c, uint color, int z, float colX, float contentX, float centerY, float half, bool rtl)
    {
        var trunkEdge = rtl ? colX + half : colX - half;
        Fill(c, color, z, MathF.Min(trunkEdge, contentX), centerY - half, MathF.Abs(contentX - trunkEdge), LineWidth);
    }

    // The x of a guide level's vertical. Level 0 is the section/group header column (outdented to the
    // header's chevron); each deeper level steps in by one tree indent. The offset is an inline-leading
    // distance: measured from the row's left edge under LTR, mirrored from the right edge under RTL, so
    // the guides track the row content (which PaddingView mirrors the same way).
    private static float ColumnX(in RectF pos, int level, bool rtl)
    {
        var offset = level == 0
            ? Spacing.Hair + Sizes.Icon / 2f
            : TreeMetrics.BaseIndent + TreeMetrics.IndentLevel * (level - 1) + TreeMetrics.ChevronWidth / 2f;
        return rtl ? pos.Right - offset : pos.Left + offset;
    }

    private static void Fill(ICanvas c, uint color, int z, float left, float bottom, float w, float h)
    {
        if (w <= 0f || h <= 0f) return;
        c.DrawRect(new DrawRectInputs
        {
            Position = new RectF(left, bottom, w, h),
            Style = new RectStyle { BackgroundColor = color },
            ZIndex = z,
        });
    }
}

// Paints a row's indent guides behind its content. Fills its parent box (the row), so all geometry
// is relative to its laid-out position.
internal sealed class TreeGuidesView : View
{
    private TreeGuides _guides;
    private uint _color;

    public TreeGuides Guides
    {
        get => _guides;
        set
        {
            if (_guides.Equals(value)) return;
            _guides = value;
            SetDirty();
        }
    }

    public uint Color
    {
        get => _color;
        set
        {
            if (_color == value) return;
            _color = value;
            SetDirty();
        }
    }

    protected override void OnDrawSelf(ICanvas c)
    {
        // The widget trees carry a Spacing.Hair gap between rows; a continuing trunk overruns its row
        // by that much at each open end so the line reads as unbroken across the gap.
        TreeGuidePainter.Draw(c, Position, _guides, _color, GetDrawZIndex(), IsRtl, gapBridge: Spacing.Hair);
    }
}

// Widget wrapper so the guides view composes into a row's box like any other child.
internal sealed record TreeGuidesWidget : Widget
{
    public Prop<TreeGuides> Guides { get; init; }
    public Prop<uint> Color { get; init; }

    protected override View CreateView(Context ctx)
    {
        var v = new TreeGuidesView();
        Guides.Apply(ctx, v, static (x, g) => x.Guides = g);
        Color.Apply(ctx, v, static (x, col) => x.Color = col);
        return v;
    }
}
