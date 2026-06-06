using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitBench;

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
    // Mirrors the bound VM's Mode so the full-file toggle button can paint its active tint.
    private readonly State<bool> _fullFileActive = new(false);

    private Action? _onOpenInWindow;
    private Action? _onToggleFullFile;

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
                        chevron,
                        new FlexItem { Grow = 1, Child = title },
                        _lfsBadge,
                        BuildFullFileToggleButton(),
                        BuildOpenInWindowButton(),
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
        _onToggleFullFile = vm.ToggleFullFile;
        vm.Mode.Subscribe(m => _fullFileActive.Value = m == DiffViewMode.FullFile);
    }

    // Toggles the diff body between the normal diff and the after-side full file. Tinted while
    // active so it reads as an engaged toggle, not a one-shot action.
    private View BuildFullFileToggleButton()
    {
        var hovered = new State<bool>(false);

        var icon = new TextView
        {
            FontFamily = LucideIcons.FontFamily,
            FontSize = 12f,
            Text = LucideIcons.FileText,
            VerticalTextAlignment = TextAlignment.Center,
            HorizontalTextAlignment = TextAlignment.Center,
            Width = 16f,
        };
        icon.BindThemedTextColor(s => _fullFileActive.Value
            ? s.DiffView.HeaderToggleActive
            : hovered.Value ? s.DiffView.HeaderTitleHover : s.DiffView.HeaderTitleIdle);

        var btn = new RectView { Children = { icon } };
        btn.UseController(_ => new HoverableButtonController(
            () => _onToggleFullFile?.Invoke(),
            h => hovered.Value = h));
        btn.Use(ctx => new Tooltip(btn, ctx, "Toggle full file", hovered, AlwaysEnabled));
        return btn;
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
        btn.Use(ctx => new Tooltip(btn, ctx, "Open in new window", hovered, AlwaysEnabled));
        return btn;
    }

    // These header buttons are always actionable, so their tooltips never need to gate on an
    // enabled state — Tooltip still requires an IReadable<bool>, so hand it a constant.
    private static readonly IReadable<bool> AlwaysEnabled = new State<bool>(true);
}
