using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Components.ContextMenu;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitBench;

// The status-bar identity chip: an author glyph + the active profile name. Clicking opens a menu
// (built by the view model) to switch profile for this repo, pin it, or manage profiles. Lives in
// the status bar, so the menu opens upward.
internal sealed class IdentityChipButton : HoverableButton
{
    public State<string> Icon { get; } = new(string.Empty);
    public State<string> Label { get; } = new(string.Empty);

    // Supplies the menu items at click time so they reflect the current profiles/resolution.
    public Func<IReadOnlyList<RepoBarContextMenu.Item>>? MenuProvider { get; set; }

    public IdentityChipButton()
    {
        var icon = new TextView
        {
            FontFamily = LucideIcons.FontFamily,
            FontSize = 12f,
            VerticalTextAlignment = TextAlignment.Center,
        };
        icon.BindText(Icon);
        icon.BindThemedTextColor(s => s.StatusBar.Text);

        var label = new TextView
        {
            FontSize = 11f,
            VerticalTextAlignment = TextAlignment.Center,
        };
        label.BindText(Label);
        label.BindThemedTextColor(s => s.StatusBar.Text);

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
        background.BindThemedBackgroundColor(s => IsHovered.Value ? s.StatusBar.IconHoverBackground : 0u);

        SetBackground(background);
    }

    protected override void OnClicked()
    {
        var ctx = Context;
        var items = MenuProvider?.Invoke();
        if (ctx == null || items == null || items.Count == 0) return;
        RepoBarContextMenu.Show(ctx, Position.TopLeft, items, MenuPlacement.Above);
    }
}
