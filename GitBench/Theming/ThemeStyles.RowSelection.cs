namespace GitBench.Theming;

/// <summary>
/// The single selection treatment shared by every navigable row list — the repo bar, the
/// branches sidebar, the commit history, and the file lists all draw their selected/hovered
/// row from this, so the highlight can't drift from one list to the next. A selected row takes
/// <see cref="Fill"/>, a merely hovered row <see cref="FillHover"/>, selected text switches to
/// <see cref="Text"/>, and a selected row in the inset-pill lists gets a leading
/// <see cref="AccentBar"/>.
/// </summary>
public sealed record RowSelectionStyles(
    uint Fill,
    uint FillHover,
    uint Text,
    uint AccentBar);

public partial record ThemeStyles
{
    private static RowSelectionStyles BuildRowSelection(ThemePalette p) =>
        new(
            Fill: p.SurfaceSelectedSubtle,
            FillHover: p.SurfaceHover,
            Text: p.RowSubtleText,
            AccentBar: p.Accent);
}
