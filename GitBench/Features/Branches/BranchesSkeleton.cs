using GitBench.Controls;
using GitBench.Features.LocalChanges;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Branches;

/// <summary>
/// The loading placeholder for the branches sidebar. Composes the real <see cref="TreeRow"/> layout —
/// section headers and indented branch rows — but fills the icon and name slots with breathing skeleton
/// blocks instead of a glyph and text, so the listing resolves into the exact same shape rather than
/// popping in over empty space. Shown only while a cold repo's branches load.
/// </summary>
internal sealed record BranchesSkeleton : Widget<BranchesSkeletonState>
{
    private const float ContentRowHeight = 26f; // matches BranchListRow's content rows
    private const float HeaderRowHeight = Sizes.RowHeight;
    private const float DotSize = 14f;
    private const float BarHeight = 9f;
    private const float HeaderBarHeight = 7f;

    // (section header?, depth, name-bar width, guide mask). Masks are built exactly as BranchTreeBuilder
    // does: each row's own elbow at its depth level (Tee = a sibling follows, Corner = last child) over
    // ancestor trunks (Through) at shallower levels. The shape faked here:
    //   LOCAL            REMOTES
    //   ├ branch         ├ folder
    //   ├ folder         │ └ branch
    //   │ └ branch       └ branch
    //   └ branch
    private static readonly (bool Header, int Depth, float Width, long Mask)[] Layout =
    {
        (true,  0, 52f,  0),
        (false, 0, 124f, TreeGuides.SetKind(0, 0, TreeGuide.Tee)),
        (false, 0, 96f,  TreeGuides.SetKind(0, 0, TreeGuide.Tee)),
        (false, 1, 108f, TreeGuides.SetKind(TreeGuides.SetKind(0, 0, TreeGuide.Through), 1, TreeGuide.Corner)),
        (false, 0, 72f,  TreeGuides.SetKind(0, 0, TreeGuide.Corner)),
        (true,  0, 64f,  0),
        (false, 0, 116f, TreeGuides.SetKind(0, 0, TreeGuide.Tee)),
        (false, 1, 88f,  TreeGuides.SetKind(TreeGuides.SetKind(0, 0, TreeGuide.Through), 1, TreeGuide.Corner)),
        (false, 0, 78f,  TreeGuides.SetKind(0, 0, TreeGuide.Corner)),
    };

    protected override BranchesSkeletonState CreateState(Context ctx) =>
        new(ctx.Require<IFrameTicker>());

    protected override IWidget Build(Context ctx, BranchesSkeletonState state)
    {
        var theme = ctx.Theme();

        // The breathing block fill: a faint overlay of the theme's neutral text color, recomputed each
        // pulse tick (and on a theme swap). dim sinks the section headers beneath the rows.
        Prop<uint> Fill(float dim) => Prop.Bind(() =>
            SkeletonPainter.Fill(theme.Styles.Value.Palette.TextPrimary, state.Pulse.Value.Value, dim));

        IWidget Block(float width, float height, float dim) => new Box
        {
            Width = width,
            Height = height,
            Background = Fill(dim),
            BorderRadius = BorderRadiusStyle.All(height / 2f),
        };

        var rows = new IWidget[Layout.Length];
        var first = true;
        for (var i = 0; i < Layout.Length; i++)
        {
            var (header, depth, width, mask) = Layout[i];
            rows[i] = header
                ? new TreeRow
                {
                    // Section headers sit at the group-header indent with the wider chevron column,
                    // mirroring BranchListRow; a between-section gap above all but the first.
                    Depth = 0,
                    RowHeight = HeaderRowHeight,
                    IndentOverride = Spacing.Hair,
                    Chevron = new Box { Width = Sizes.Icon },
                    SpacingBefore = first ? 0 : Spacing.Lg,
                    NameSlot = Block(width, HeaderBarHeight, dim: 0.6f),
                }
                : new TreeRow
                {
                    Depth = depth,
                    RowHeight = ContentRowHeight,
                    // Same convention as BranchListRow: levels = depth + 1, level 0 being the section
                    // header's column. TreeRow paints the connectors in the shared indent-guide color.
                    Guides = new TreeGuides(mask, depth + 1),
                    GlyphSlot = new Row
                    {
                        Width = TreeMetrics.IconWidth,
                        MainAxis = MainAxisAlignment.Center,
                        CrossAxis = CrossAxisAlignment.Center,
                        Children = [Block(DotSize, DotSize, dim: 1f)],
                    },
                    NameSlot = Block(width, BarHeight, dim: 1f),
                };
            first = false;
        }

        return new Padding
        {
            Amount = new PaddingStyle { Left = Spacing.Md, Top = Spacing.Md, Bottom = Spacing.Md },
            Children = [new Column { Gap = Spacing.Hair, CrossAxis = CrossAxisAlignment.Stretch, Children = rows }],
        };
    }
}

/// <summary>Owns the skeleton's <see cref="Pulse"/> for its mounted lifetime (started on build, stopped
/// on unmount), so the breathe — and the frame loop it drives — runs only while the skeleton is shown.</summary>
internal sealed class BranchesSkeletonState : IDisposable
{
    public Pulse Pulse { get; }

    public BranchesSkeletonState(IFrameTicker ticker)
    {
        Pulse = new Pulse(ticker);
        Pulse.Start();
    }

    public void Dispose() => Pulse.Dispose();
}
