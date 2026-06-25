using GitBench.Controls;
using GitBench.Features.LocalChanges;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Commits;

/// <summary>
/// The loading placeholder for the commit details panel. Reproduces the fixed two-region layout —
/// the author/message header above, the changes list below, split by the same
/// <see cref="VerticalSplitContainer"/> the real view uses — with breathing skeleton blocks in place of
/// the avatar, message lines, and file rows, so details resolve into the same shape rather than popping
/// in over a centered spinner. Shown only while a commit's details load with nothing prior to keep on
/// screen (a cold load); a commit-to-commit reload keeps the previous details up instead.
/// </summary>
internal sealed record CommitDetailsSkeleton : Widget
{
    private const float AvatarSize = 36f;       // matches CommitDetailsView's avatar
    private const float RowHeight = FileChangesUI.RowHeight;
    private const float BadgeSize = FileChangesUI.BadgeSize;
    private const int FileRowCount = 8;

    // Varied path-bar widths so the file rows read as distinct paths rather than a uniform grid.
    private static readonly float[] FileBarWidths = { 168f, 224f, 132f, 196f, 150f, 208f, 120f, 178f };

    protected override View CreateView(Context ctx)
    {
        var theme = ctx.Theme();
        var pulse = new Pulse(ctx.Require<IFrameTicker>());
        pulse.Start();

        // The breathing block fill: a faint overlay of the theme's neutral text color, recomputed each
        // pulse tick (and on a theme swap). dim sinks secondary blocks beneath the primary ones.
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
                    Amount = new PaddingStyle { Left = (int)FileChangesUI.RowPaddingLeft, Right = (int)FileChangesUI.RowPaddingRight },
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

        // Author header: avatar circle + name/date bars, mirroring CommitDetailsView.BuildAuthorHeader.
        var author = new Row
        {
            Gap = Spacing.Md,
            CrossAxis = CrossAxisAlignment.Center,
            Children =
            [
                Bar(AvatarSize, AvatarSize),
                new Column { Gap = Spacing.Sm, Children = [Bar(130f, 10f), Bar(90f, 8f, dim: 0.7f)] },
            ],
        };

        var header = new Padding
        {
            Amount = PaddingStyle.All(14), // CommitDetailsView.Padding
            Children =
            [
                new Column
                {
                    Gap = Spacing.Md,
                    Children =
                    [
                        author,
                        Bar(240f, 11f),            // subject
                        Bar(280f, 8f, dim: 0.8f),  // body
                        Bar(200f, 8f, dim: 0.8f),  // body
                        Bar(220f, 8f, dim: 0.7f),  // commit line
                        Bar(160f, 8f, dim: 0.7f),  // parent line
                    ],
                },
            ],
        };

        // Changes region: the section header bar (real chrome, skeleton title) over file rows.
        var changesHeader = new Box
        {
            Background = Prop.Bind(() => theme.Styles.Value.FileChangesSection.HeaderBackground),
            BorderSize = new BorderSizeStyle { Top = 1, Bottom = 1 },
            BorderColor = Prop.Bind(() => new BorderColorStyle
            {
                Top = theme.Styles.Value.FileChangesSection.HeaderBorder,
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

        var changesBody = new Padding
        {
            Amount = new PaddingStyle { Left = Spacing.Sm, Right = Spacing.Sm, Top = Spacing.Sm },
            Children = [new Column { CrossAxis = CrossAxisAlignment.Stretch, Children = rows }],
        };

        var changes = new Column
        {
            CrossAxis = CrossAxisAlignment.Stretch,
            Children = [changesHeader, changesBody],
        };

        // Split via the real container at the same fraction (and splitter) as CommitDetailsView's inner
        // split, so the loaded changes panel lands exactly where the skeleton's does — no jump.
        var splitter = new RectView();
        splitter.BindThemedBackgroundColor(theme, s => s.CommitDetailsView.SplitterIdle);
        var split = new VerticalSplitContainer(header.BuildView(ctx), changes.BuildView(ctx), splitter, bottomFraction: 1f / 2f)
        {
            BottomVisible = true,
        };
        split.Use(() => pulse); // stops the pulse (and its frame loop) on unmount
        return split;
    }
}
