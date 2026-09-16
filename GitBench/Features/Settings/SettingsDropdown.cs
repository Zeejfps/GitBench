using GitBench.Controls;
using GitBench.Features.Repos;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Settings;

/// <summary>A Settings row's picker: shows the current choice and opens the choices beneath it.</summary>
internal sealed record SettingsDropdown : Widget
{
    public required Prop<string?> Selected { get; init; }

    /// <summary>Read when the menu opens, so the check mark tracks the live choice rather than
    /// whichever one was current when the dialog was built.</summary>
    public required Func<IReadOnlyList<RepoBarContextMenu.Item>> Options { get; init; }

    protected override IWidget Build(Context ctx) => new DropdownWidget
    {
        Width = Width,
        Height = Height,
        Children =
        [
            new Grow
            {
                Child = new Text
                {
                    Value = Selected,
                    FontSize = FontSize.Body,
                    VAlign = TextAlignment.Center,
                    Color = Theme.Color(s => s.Palette.TextPrimary),
                },
            },
        ],
    }.WithMenuController(rect => RepoBarContextMenu.Show(ctx, rect.BottomLeft, Options()));
}
