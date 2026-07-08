using GitBench.Controls;
using GitBench.Features.LocalChanges;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Review;

/// <summary>
/// The loading placeholder for the review window's left column (the file tree) during an optimistic
/// base switch: the real "Changes" section-header chrome over breathing skeleton file rows, so the
/// new range's list resolves into the same shape rather than popping in over blank space. Mirrors the
/// file-row skeleton the commit-details panel uses. Owns a <see cref="Pulse"/> for its mounted
/// lifetime (started on build, stopped on unmount).
/// </summary>
internal sealed record ReviewTreeSkeleton : Widget
{
    private const float RowHeight = FileChangesUI.RowHeight;
    private const float BadgeSize = FileChangesUI.BadgeSize;
    private const int FileRowCount = 12;

    // Varied path-bar widths so the rows read as distinct paths rather than a uniform grid.
    private static readonly float[] FileBarWidths =
        { 168f, 224f, 132f, 196f, 150f, 208f, 120f, 178f, 146f, 190f, 112f, 202f };

    protected override View CreateView(Context ctx)
    {
        var theme = ctx.Theme();
        var pulse = new Pulse(ctx.Require<IFrameTicker>());
        pulse.Start();

        // The breathing block fill: a faint overlay of the theme's neutral text color, recomputed each
        // pulse tick (and on a theme swap). dim sinks the section header beneath the rows.
        Prop<uint> Fill(float dim) => Prop.Bind(() =>
            SkeletonPainter.Fill(theme.Styles.Value.Palette.TextPrimary, pulse.Value.Value, dim));
        IWidget Bar(float width, float height, float dim = 1f) => new Box
        {
            Width = width,
            Height = height,
            Background = Fill(dim),
            BorderRadius = BorderRadiusStyle.All(height / 2f),
        };

        // One file-row skeleton: a status-badge square then a path bar, at the real row height and indent.
        IWidget FileRow(float width) => new Box
        {
            Height = RowHeight,
            Children =
            [
                new Padding
                {
                    Amount = new PaddingStyle
                    {
                        Left = (int)FileChangesUI.RowPaddingLeft,
                        Right = (int)FileChangesUI.RowPaddingRight,
                    },
                    Children =
                    [
                        new Row
                        {
                            Gap = FileChangesUI.BadgeGap,
                            CrossAxis = CrossAxisAlignment.Center,
                            Children = [Bar(BadgeSize, BadgeSize), Bar(width, 8f)],
                        },
                    ],
                },
            ],
        };

        // The section header bar (real chrome, skeleton title), mirroring the "Changes" header.
        var header = new Box
        {
            Background = Prop.Bind(() => theme.Styles.Value.FileChangesSection.HeaderBackground),
            BorderSize = new BorderSizeStyle { Bottom = 1 },
            BorderColor = Prop.Bind(() => new BorderColorStyle
            {
                Bottom = theme.Styles.Value.FileChangesSection.HeaderBorder,
            }),
            Children =
            [
                new Padding
                {
                    Amount = PaddingStyle.All(FileChangesUI.HeaderPadding),
                    Children = [Bar(72f, 8f, dim: 0.8f)],
                },
            ],
        };

        var rows = new IWidget[FileRowCount];
        for (var i = 0; i < FileRowCount; i++)
            rows[i] = FileRow(FileBarWidths[i % FileBarWidths.Length]);

        var body = new Padding
        {
            Amount = new PaddingStyle { Left = Spacing.Sm, Right = Spacing.Sm, Top = Spacing.Sm },
            Children = [new Column { CrossAxis = CrossAxisAlignment.Stretch, Children = rows }],
        };

        var view = new Column
        {
            CrossAxis = CrossAxisAlignment.Stretch,
            Children = [header, body],
        }.BuildView(ctx);
        view.Use(() => pulse); // stops the pulse (and its frame loop) on unmount
        return view;
    }
}
