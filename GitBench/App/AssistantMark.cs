using GitBench.Controls;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.App;

/// <summary>
/// The assistant's dino mark at a given square size, shown wherever the assistant is offered.
/// Falls back to an accent glyph when the mark image isn't loaded.
/// </summary>
/// <remarks>
/// Both cases render into one shared square, so the image and the glyph cannot end up aligned
/// differently. The square is centered rather than handed straight to the parent because an image is
/// drawn into whatever rect layout gives it — a cross-axis <c>Stretch</c>, which is how
/// <see cref="ButtonWidget"/> lays out its content, would otherwise size the mark to the button
/// instead of to <see cref="Size"/>. A font glyph never had either problem, which is why the Lucide
/// icons beside it in the toolbar always looked right.
/// </remarks>
internal sealed record AssistantMark : Widget
{
    /// <summary>
    /// Image id of the mark, set by startup once it's loaded into the canvas. Observable because
    /// the root content mounts before startup gets to load the image — a mark built early swaps
    /// from the glyph fallback when the id lands. Stays null on load failure.
    /// </summary>
    public static readonly State<string?> ImageId = new(null);

    public int Size { get; init; } = 18;

    protected override IWidget Build(Context ctx) => new Column
    {
        Width = Size,
        MainAxis = MainAxisAlignment.Center,
        CrossAxis = CrossAxisAlignment.Center,
        Children =
        [
            new Box
            {
                Width = Size,
                Height = Size,
                Children =
                [
                    new Switch<string?>
                    {
                        Value = ImageId,
                        Case = id => id != null
                            ? new Image { ImageId = id, Width = Size, Height = Size }
                            : new Text
                            {
                                Value = LucideIcons.SquareTerminal,
                                FontFamily = LucideIcons.FontFamily,
                                FontSize = Size,
                                HAlign = TextAlignment.Center,
                                VAlign = TextAlignment.Center,
                                Color = Theme.Color(s => s.Palette.Accent),
                            },
                    },
                ],
            },
        ],
    };
}
