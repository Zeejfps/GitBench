using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitGui;

/// <summary>
/// Header strip for the embedded diff panes (Local Changes, Commit Details). Owns the
/// collapse state for the pane, shows the "Diff View" title, an LFS badge, and a button to
/// pop the diff into its own window. Clicking anywhere on the bar toggles collapse; nested
/// buttons consume their own clicks. The diff body itself lives in a sibling
/// <see cref="DiffView"/>; this bar carries no staging controls (that lives in the file
/// lists for embedded panes, and in <see cref="DiffWindowToolbar"/> for the pop-out window).
/// </summary>
internal sealed class DiffPaneHeader : MultiChildView, IBind<DiffViewModel>
{
    // Height of the always-visible header strip. Exposed so the parent split container can pin
    // the collapsed pane to exactly this height, keeping the chevron clickable.
    public const float HeaderHeight = 24f;

    private readonly State<bool> _isCollapsed = new(false);
    private readonly LfsBadgeView _lfsBadge = new();

    private Action? _onOpenInWindow;

    public DiffPaneHeader()
    {
        var hovered = new State<bool>(false);

        var title = new TextView
        {
            Text = "Diff View",
            FontSize = 12f,
            VerticalTextAlignment = TextAlignment.Center,
        };
        title.BindThemedTextColor(s => hovered.Value ? s.DiffView.HeaderTitleHover : s.DiffView.HeaderTitleIdle);

        var chevron = new TextView
        {
            FontFamily = LucideIcons.FontFamily,
            FontSize = 12f,
            VerticalTextAlignment = TextAlignment.Center,
            HorizontalTextAlignment = TextAlignment.Center,
            Width = 16f,
        };
        chevron.BindText(_isCollapsed, c => c ? LucideIcons.ChevronUp : LucideIcons.ChevronDown);
        chevron.BindThemedTextColor(s => hovered.Value ? s.DiffView.HeaderTitleHover : s.DiffView.HeaderTitleIdle);

        var bar = new RectView
        {
            Height = HeaderHeight,
            BorderSize = new BorderSizeStyle { Top = 1, Bottom = 1 },
            Padding = new PaddingStyle { Left = 8, Right = 6 },
            Children =
            {
                new FlexRowView
                {
                    Gap = 6f,
                    CrossAxisAlignment = CrossAxisAlignment.Center,
                    Children =
                    {
                        new FlexItem { Grow = 1, Child = title },
                        BuildOpenInWindowButton(),
                        _lfsBadge,
                        chevron,
                    },
                },
            },
        };
        bar.BindThemedBackgroundColor(s =>
            hovered.Value ? s.DiffView.HeaderBackgroundHover : s.DiffView.HeaderBackgroundIdle);
        bar.BindThemedBorderColor(s => new BorderColorStyle
        {
            Top = s.DiffView.HeaderBorderTop,
            Bottom = s.DiffView.HeaderBorderBottom,
        });

        // Bubble phase only: the bar wraps the interactive open-in-window button. Registering
        // on capture (the default) would let the bar consume that click before it reaches the
        // button — bubble lets the button consume first, and clicks on the bar's empty area
        // still bubble up here to toggle collapse.
        bar.UseController(_ => new HoverableButtonController(
            () => _isCollapsed.Value = !_isCollapsed.Value,
            h => hovered.Value = h), EventPhaseFilter.Bubble);

        AddChildToSelf(bar);
    }

    public IReadable<bool> IsCollapsed => _isCollapsed;

    public void Bind(DiffViewModel vm)
    {
        vm.LfsStatus.Subscribe(_lfsBadge.SetStatus);
        _onOpenInWindow = vm.RequestOpenInWindow;
    }

    private View BuildOpenInWindowButton()
    {
        var hovered = new State<bool>(false);

        var icon = new TextView
        {
            FontFamily = LucideIcons.FontFamily,
            FontSize = 12f,
            Text = LucideIcons.ExternalLink,
            VerticalTextAlignment = TextAlignment.Center,
            HorizontalTextAlignment = TextAlignment.Center,
            Width = 16f,
        };
        icon.BindThemedTextColor(s => hovered.Value ? s.DiffView.HeaderTitleHover : s.DiffView.HeaderTitleIdle);

        var btn = new RectView { Children = { icon } };
        btn.UseController(_ => new HoverableButtonController(
            () => _onOpenInWindow?.Invoke(),
            h => hovered.Value = h));
        return btn;
    }
}
