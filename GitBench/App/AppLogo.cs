using GitBench.Controls;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.App;

/// <summary>
/// The app icon at a given square size, shared by the About dialog and the welcome screen.
/// Falls back to an accent glyph when the icon image isn't loaded.
/// </summary>
internal sealed record AppLogo : Widget
{
    /// <summary>
    /// Image id of the app icon, set by startup once it's loaded into the canvas. Observable
    /// because the root content mounts before startup gets to load the image — a logo built
    /// early swaps from the glyph fallback when the id lands. Stays null on load failure.
    /// </summary>
    public static readonly State<string?> IconImageId = new(null);

    public int Size { get; init; } = 84;

    protected override IWidget Build(Context ctx) => new Switch<string?>
    {
        Value = IconImageId,
        Case = id => id != null
            ? new Image { ImageId = id, Width = Size, Height = Size }
            : new Box
            {
                Width = Size,
                Height = Size,
                Children =
                [
                    new Text
                    {
                        Value = LucideIcons.FolderGit2,
                        FontFamily = LucideIcons.FontFamily,
                        FontSize = FontSize.Hero,
                        HAlign = TextAlignment.Center,
                        VAlign = TextAlignment.Center,
                        Color = Theme.Color(s => s.Palette.Accent),
                    },
                ],
            },
    };
}
