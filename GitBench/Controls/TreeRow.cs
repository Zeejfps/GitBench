using GitBench.Features.LocalChanges;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Controls;

/// <summary>
/// The shared visual for a row in any of the app's tree views (repo bar, branches sidebar): an
/// indent sized to <see cref="Depth"/>, a fixed-width chevron column, an optional kind glyph, the
/// row name, and an optional trailing slot (a status dot, an ahead/behind badge). Purely props — it
/// knows nothing about view models, selection, or input — so each feature composes it and supplies
/// its own glyph, colors, chevron, and trailing widget. Column widths come from <see cref="TreeMetrics"/>
/// so every tree shares one rhythm.
/// </summary>
internal sealed record TreeRow : Widget
{
    public required int Depth { get; init; }
    public required float RowHeight { get; init; }

    // Explicit left indent in pixels, overriding the depth-derived one. Section headers use it to sit
    // at the repo bar's group-header indent rather than the content indent.
    public int? IndentOverride { get; init; }

    // The chevron column. A fold toggle for collapsible rows; null reserves the column so a leaf row's
    // glyph still aligns under its siblings' glyphs.
    public IWidget? Chevron { get; init; }

    // Lucide glyph drawn before the name; null leaves no icon column (section/remote headers).
    public string? Glyph { get; init; }
    public float GlyphSize { get; init; } = 13f;
    public Prop<uint> IconColor { get; init; }

    public Prop<string?> Name { get; init; }
    public Prop<uint> NameColor { get; init; }
    public Prop<FontWeight> NameWeight { get; init; }

    // Name font size; unset uses the canvas default (the size branch/repo rows draw at). Section
    // headers pass a caption size so they match the repo bar's group headers.
    public Prop<float> NameSize { get; init; }

    // Fill behind the row (hover tint, or 0 when the floating selection bar owns this row's fill).
    public Prop<uint> Background { get; init; }

    public IWidget? Trailing { get; init; }

    // Empty space reserved above the row, for the gap that separates one section from the next (the
    // repo bar's between-group spacing). Dead at rest; defaults to none.
    public int SpacingBefore { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var leftPad = IndentOverride ?? ((int)TreeMetrics.BaseIndent + (int)TreeMetrics.IndentLevel * Depth);

        var row = new List<IWidget>(4) { Chevron ?? new Box { Width = TreeMetrics.ChevronWidth } };
        if (Glyph is { } glyph)
            row.Add(new Text
            {
                Value = glyph,
                FontFamily = LucideIcons.FontFamily,
                FontSize = GlyphSize,
                Width = TreeMetrics.IconWidth,
                HAlign = TextAlignment.Center,
                VAlign = TextAlignment.Center,
                Color = IconColor,
            });
        row.Add(new Grow
        {
            Child = new Text
            {
                Value = Name,
                FontSize = NameSize,
                HAlign = TextAlignment.Start,
                VAlign = TextAlignment.Center,
                Overflow = TextOverflow.Ellipsis,
                Color = NameColor,
                Weight = NameWeight,
            },
        });
        if (Trailing is { } trailing)
            row.Add(trailing);

        var box = new Box
        {
            Height = RowHeight,
            Background = Background,
            Children =
            [
                new Padding
                {
                    Amount = new PaddingStyle { Left = leftPad, Right = Spacing.Lg },
                    Children =
                    [
                        new Row
                        {
                            Gap = TreeMetrics.ColumnGap,
                            CrossAxis = CrossAxisAlignment.Center,
                            Children = row.ToArray(),
                        },
                    ],
                },
            ],
        };

        return SpacingBefore > 0
            ? new Padding { Amount = new PaddingStyle { Top = SpacingBefore }, Children = [box] }
            : box;
    }
}
