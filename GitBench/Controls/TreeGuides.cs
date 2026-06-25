using System;
using GitBench.Features.LocalChanges;
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

// A row's full set of ancestry guides, one <see cref="TreeGuide"/> per depth level, packed two bits per
// level into a long so it travels as a cheap value (value equality lets a bound view skip redundant
// repaints and the branches' value-keyed row list reuse unchanged rows). Levels run 0..Depth-1; the
// deepest level is the row's own connector, the shallower ones its ancestors' trunks.
internal readonly struct TreeGuides : IEquatable<TreeGuides>
{
    private readonly long _mask;

    public TreeGuides(long mask, int depth)
    {
        _mask = mask;
        Depth = depth;
    }

    public int Depth { get; }

    public TreeGuide At(int level) => (TreeGuide)((_mask >> (level * 2)) & 0b11);

    public static long SetKind(long mask, int level, TreeGuide kind) =>
        (mask & ~(0b11L << (level * 2))) | ((long)kind << (level * 2));

    public bool Equals(TreeGuides other) => _mask == other._mask && Depth == other.Depth;
    public override bool Equals(object? obj) => obj is TreeGuides g && Equals(g);
    public override int GetHashCode() => HashCode.Combine(_mask, Depth);
}

// Paints a row's indent guides behind its content: a vertical hairline per ancestry level, plus the
// elbow that connects the row to its parent. Fills its parent box (the row), so all geometry is relative
// to its laid-out position and column widths come from the shared <see cref="TreeMetrics"/>.
internal sealed class TreeGuidesView : View
{
    private const float LineWidth = 1f;

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
        var depth = _guides.Depth;
        if (depth <= 0 || (_color >> 24) == 0) return;

        var z = GetDrawZIndex();
        var pos = Position;
        var half = LineWidth / 2f;
        var centerY = pos.Bottom + pos.Height / 2f;
        // Where the elbow lands: the chevron column of the row's own indent.
        var contentX = pos.Left + TreeMetrics.BaseIndent + TreeMetrics.IndentLevel * depth + TreeMetrics.ChevronWidth / 2f;

        for (var level = 0; level < depth; level++)
        {
            var kind = _guides.At(level);
            if (kind == TreeGuide.None) continue;

            var colX = pos.Left + TreeMetrics.BaseIndent + TreeMetrics.IndentLevel * level + TreeMetrics.ChevronWidth / 2f;
            switch (kind)
            {
                case TreeGuide.Through:
                    Fill(c, z, colX - half, pos.Bottom, LineWidth, pos.Height);
                    break;
                case TreeGuide.Tee:
                    Fill(c, z, colX - half, pos.Bottom, LineWidth, pos.Height);
                    Fill(c, z, colX - half, centerY - half, contentX - (colX - half), LineWidth);
                    break;
                case TreeGuide.Corner:
                    Fill(c, z, colX - half, centerY, LineWidth, pos.Height / 2f);
                    Fill(c, z, colX - half, centerY - half, contentX - (colX - half), LineWidth);
                    break;
            }
        }
    }

    private void Fill(ICanvas c, int z, float left, float bottom, float w, float h)
    {
        if (w <= 0f || h <= 0f) return;
        c.DrawRect(new DrawRectInputs
        {
            Position = new RectF(left, bottom, w, h),
            Style = new RectStyle { BackgroundColor = _color },
            ZIndex = z,
        });
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
