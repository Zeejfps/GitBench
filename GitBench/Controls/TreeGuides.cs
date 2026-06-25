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

// Paints a row's indent guides behind its content: a vertical hairline per ancestry level, plus the
// elbow that connects the row to its parent. Fills its parent box (the row), so all geometry is relative
// to its laid-out position and column widths come from the shared <see cref="TreeMetrics"/>.
internal sealed class TreeGuidesView : View
{
    private const float LineWidth = 1f;
    // The rows carry a small gap between them (Spacing.Hair, shared by both trees). A continuing trunk
    // overruns its row by that much at each open end so the line reads as unbroken across the gap.
    private const float GapBridge = Spacing.Hair;

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
        var levels = _guides.Levels;
        if (levels <= 0 || (_color >> 24) == 0) return;

        var z = GetDrawZIndex();
        var pos = Position;
        var half = LineWidth / 2f;
        var centerY = pos.Bottom + pos.Height / 2f;
        // Where the elbow lands: the chevron column of the row's own indent (its tree depth is levels-1).
        var contentX = ColumnX(pos.Left, levels);

        for (var level = 0; level < levels; level++)
        {
            var kind = _guides.At(level);
            if (kind == TreeGuide.None) continue;

            var colX = ColumnX(pos.Left, level);
            switch (kind)
            {
                case TreeGuide.Through:
                    // Continues both above and below: overrun both gaps.
                    Fill(c, z, colX - half, pos.Bottom - GapBridge, LineWidth, pos.Height + 2f * GapBridge);
                    break;
                case TreeGuide.Tee:
                    // Connects up to its parent/siblings and on down to the next sibling: overrun both.
                    Fill(c, z, colX - half, pos.Bottom - GapBridge, LineWidth, pos.Height + 2f * GapBridge);
                    Fill(c, z, colX - half, centerY - half, contentX - (colX - half), LineWidth);
                    break;
                case TreeGuide.Corner:
                    // Last child: the trunk ends here, so only overrun the gap above to reach its parent.
                    Fill(c, z, colX - half, centerY, LineWidth, pos.Height / 2f + GapBridge);
                    Fill(c, z, colX - half, centerY - half, contentX - (colX - half), LineWidth);
                    break;
            }
        }
    }

    // The x of a guide level's vertical, relative to the row's left edge. Level 0 is the section/group
    // header column (outdented to the header's chevron); each deeper level steps in by one tree indent.
    private static float ColumnX(float left, int level) => level == 0
        ? left + Spacing.Hair + Sizes.Icon / 2f
        : left + TreeMetrics.BaseIndent + TreeMetrics.IndentLevel * (level - 1) + TreeMetrics.ChevronWidth / 2f;

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
