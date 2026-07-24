using GitBench.Controls;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Review;

/// <summary>
/// The loading placeholder for a stacked-diff column: a few file cards — the real card chrome
/// (header band, outline, diff surface) over breathing skeleton blocks — at the geometry
/// <see cref="ReviewDiffListView"/> lays its sections out with, so the range's diffs resolve into the
/// same shape rather than popping in over blank space. Owns a <see cref="Pulse"/> for its mounted
/// lifetime (started on build, stopped on unmount).
/// </summary>
internal sealed record ReviewDiffSkeleton : Widget
{
    // Mirrors ReviewDiffListView's card geometry.
    private const float PanelPaddingX = 12f;
    private const float PanelPaddingY = 24f;
    private const float SectionGap = 12f;
    private const float HeaderBandHeight = 38f;
    private const float HeaderPaddingX = 10f;
    private const float ChevronWidth = 16f;
    private const float StatusIconWidth = 18f;
    // The list's rows are one measured mono line tall; the skeleton has no text to measure, so it
    // approximates that at the body font size.
    private const float BodyLineHeight = 18f;
    private const float GutterWidth = 22f;

    private static readonly int[] CardLines = { 7, 4, 9 };
    private static readonly float[] HeaderPathWidths = { 206f, 148f, 262f };

    // Varied code-line widths so the body reads as source rather than a block.
    private static readonly float[] LineWidths =
        { 312f, 218f, 386f, 264f, 172f, 340f, 228f, 296f, 194f, 358f, 244f, 180f };

    protected override View CreateView(Context ctx)
    {
        var theme = ctx.Theme();
        var pulse = new Pulse(ctx.Require<IFrameTicker>());
        pulse.Start();

        Prop<uint> Fill(float dim) => Prop.Bind(() =>
            SkeletonPainter.Fill(theme.Styles.Value.Palette.TextPrimary, pulse.Value.Value, dim));
        IWidget Bar(float width, float height, float dim = 1f) => new Box
        {
            Width = width,
            Height = height,
            Background = Fill(dim),
            BorderRadius = BorderRadiusStyle.All(height / 2f),
        };

        // The card's header band: fold chevron, status icon, path — the real band chrome, so the
        // loaded header lands on the same line.
        IWidget Header(float pathWidth) => new Box
        {
            Height = HeaderBandHeight,
            Background = Theme.Color(s => s.FileChangesSection.HeaderBackground),
            BorderSize = BorderSizeStyle.All(1),
            BorderColor = Theme.BorderColor(s => BorderColorStyle.All(s.Palette.Border)),
            Children =
            [
                new Padding
                {
                    Amount = new PaddingStyle { Left = (int)HeaderPaddingX, Right = (int)HeaderPaddingX },
                    Children =
                    [
                        new Row
                        {
                            Gap = 6f,
                            CrossAxis = CrossAxisAlignment.Center,
                            Children =
                            [
                                Bar(ChevronWidth, 8f, dim: 0.7f),
                                Bar(StatusIconWidth, 10f),
                                Bar(pathWidth, 9f),
                            ],
                        },
                    ],
                },
            ],
        };

        // One diff line: the line-number gutter then the code.
        IWidget Line(int index) => new Row
        {
            Height = BodyLineHeight,
            Gap = 10f,
            CrossAxis = CrossAxisAlignment.Center,
            Children =
            [
                Bar(GutterWidth, 7f, dim: 0.6f),
                Bar(LineWidths[index % LineWidths.Length], 8f, dim: 0.85f),
            ],
        };

        IWidget Body(int lineCount, int seed)
        {
            var lines = new IWidget[lineCount];
            for (var i = 0; i < lineCount; i++) lines[i] = Line(seed + i);
            return new Box
            {
                Background = Theme.Color(s => s.DiffContent.Background),
                BorderSize = new BorderSizeStyle { Left = 1, Right = 1, Bottom = 1 },
                BorderColor = Theme.BorderColor(s => new BorderColorStyle
                {
                    Left = s.Palette.Border,
                    Right = s.Palette.Border,
                    Bottom = s.Palette.Border,
                }),
                Children =
                [
                    new Padding
                    {
                        Amount = new PaddingStyle { Left = (int)HeaderPaddingX, Right = (int)HeaderPaddingX, Top = 6, Bottom = 6 },
                        Children = [new Column { Children = lines }],
                    },
                ],
            };
        }

        var cards = new IWidget[CardLines.Length];
        var lineSeed = 0;
        for (var i = 0; i < CardLines.Length; i++)
        {
            cards[i] = new Column
            {
                CrossAxis = CrossAxisAlignment.Stretch,
                Children = [Header(HeaderPathWidths[i % HeaderPathWidths.Length]), Body(CardLines[i], lineSeed)],
            };
            lineSeed += CardLines[i];
        }

        // Clipped: the cards are a fixed stack, so on a short viewport they must stop at the column's
        // edge rather than paint over the footer.
        var view = new Clipped
        {
            Child = new Padding
            {
                Amount = new PaddingStyle
                {
                    Left = (int)PanelPaddingX,
                    Right = (int)PanelPaddingX,
                    Top = (int)PanelPaddingY,
                },
                Children =
                [
                    new Column { CrossAxis = CrossAxisAlignment.Stretch, Gap = SectionGap, Children = cards },
                ],
            },
        }.BuildView(ctx);
        view.Use(() => pulse); // stops the pulse (and its frame loop) on unmount
        return view;
    }
}
