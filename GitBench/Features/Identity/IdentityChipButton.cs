using GitBench.Controls;
using GitBench.Features.Repos;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Components.ContextMenu;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Identity;

// The status-bar identity chip: an author glyph + the active profile name. Clicking opens a menu
// (built by the view model) to switch profile for this repo, pin it, or manage profiles. Lives in
// the status bar, so the menu opens upward.
internal sealed record IdentityChipButton : Widget<ButtonState>
{
    /// <summary>Author glyph.</summary>
    public required Prop<string?> Icon { get; init; }

    /// <summary>Active profile name.</summary>
    public required Prop<string?> Label { get; init; }

    // Supplies the menu items at click time so they reflect the current profiles/resolution.
    public required Func<IReadOnlyList<RepoBarContextMenu.Item>> MenuProvider { get; init; }

    protected override ButtonState CreateState(Context ctx) => new(null);

    protected override IWidget Build(Context ctx, ButtonState state)
    {
        return new Box
        {
            BorderRadius = BorderRadiusStyle.All(4),
            Background = Theme.Color(s => s.StatusBar.IconButtonBackground(state)),
            Children =
            [
                new Padding
                {
                    Amount = new PaddingStyle { Left = 4, Right = 4 },
                    Children =
                    [
                        new Row
                        {
                            Gap = 4,
                            CrossAxis = CrossAxisAlignment.Center,
                            Children =
                            [
                                new Text
                                {
                                    FontFamily = LucideIcons.FontFamily,
                                    FontSize = 12f,
                                    VAlign = TextAlignment.Center,
                                    Value = Icon,
                                    Color = Theme.Color(s => s.StatusBar.Text),
                                },
                                new Text
                                {
                                    FontSize = 11f,
                                    VAlign = TextAlignment.Center,
                                    Value = Label,
                                    Color = Theme.Color(s => s.StatusBar.Text),
                                },
                            ],
                        },
                    ],
                },
            ],
        }.WithController(ctx, v => new MenuButtonController(v, state, anchor =>
        {
            var items = MenuProvider();
            if (items.Count == 0) return;
            RepoBarContextMenu.Show(ctx, anchor, items, MenuPlacement.Above);
        }));
    }
}
