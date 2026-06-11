using GitBench.Controls;
using GitBench.Features.Repos;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Components.ContextMenu;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitBench.Features.Identity;

// The status-bar identity chip: an author glyph + the active profile name. Clicking opens a menu
// (built by the view model) to switch profile for this repo, pin it, or manage profiles. Lives in
// the status bar, so the menu opens upward.
internal sealed class IdentityChipButton : HoverableButton
{
    private readonly Context _ctx;

    public State<string> Icon { get; } = new(string.Empty);
    public State<string> Label { get; } = new(string.Empty);

    // Supplies the menu items at click time so they reflect the current profiles/resolution.
    public Func<IReadOnlyList<RepoBarContextMenu.Item>>? MenuProvider { get; set; }

    public IdentityChipButton(Context ctx)
    {
        _ctx = ctx;
        var theme = ctx.Theme();

        var icon = new TextView(ctx.Canvas)
        {
            FontFamily = LucideIcons.FontFamily,
            FontSize = 12f,
            VerticalTextAlignment = TextAlignment.Center,
        };
        icon.BindText(Icon);
        icon.BindTextColor(() => theme.Styles.Value.StatusBar.Text);

        var label = new TextView(ctx.Canvas)
        {
            FontSize = 11f,
            VerticalTextAlignment = TextAlignment.Center,
        };
        label.BindText(Label);
        label.BindTextColor(() => theme.Styles.Value.StatusBar.Text);

        var background = new RectView
        {
            BorderRadius = BorderRadiusStyle.All(4),
            Padding = new PaddingStyle { Left = 4, Right = 4 },
            Children =
            {
                new FlexRowView
                {
                    Gap = 4,
                    CrossAxisAlignment = CrossAxisAlignment.Center,
                    Children = { icon, label },
                },
            },
        };
        background.BindBackgroundColor(() => IsHovered.Value ? theme.Styles.Value.StatusBar.IconHoverBackground : 0u);

        SetBackground(background);
    }

    protected override void OnClicked()
    {
        var items = MenuProvider?.Invoke();
        if (items == null || items.Count == 0) return;
        RepoBarContextMenu.Show(_ctx, Position.TopLeft, items, MenuPlacement.Above);
    }
}
