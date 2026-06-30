using GitBench.Controls;
using GitBench.Features.Diff;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Commits;

/// <summary>
/// The tab strip across the top of the commit-details (metadata/diff) region: a fixed "Details" tab
/// (commit metadata) followed by one tab per open file. The active tab takes the shared row-selection
/// fill; file tabs carry a close button. Clicking a tab activates it; opening a file from the list
/// adds or focuses its tab through the <see cref="CommitDetailsViewModel"/>.
/// </summary>
internal sealed record CommitDetailsTabStrip : Widget
{
    public const float StripHeight = 32f;

    public required CommitDetailsViewModel Vm { get; init; }

    protected override IWidget Build(Context ctx) => new Box
    {
        Height = StripHeight,
        Background = Theme.Color(s => s.CommitDetailsView.Background),
        BorderSize = new BorderSizeStyle { Bottom = 1 },
        BorderColor = Theme.BorderColor(s => new BorderColorStyle { Bottom = s.Palette.Border }),
        Children =
        [
            // Reuses the Actions toolbar's scrollbar-less horizontal scroller: once the tabs overflow
            // the strip it clips them and the wheel — the vertical wheel included — pans it sideways.
            new HorizontalScrollArea
            {
                Child = new Row
                {
                    CrossAxis = CrossAxisAlignment.Stretch,
                    Children =
                    [
                        new CommitDetailsTab { Vm = Vm },
                        Each.Of(Vm.OpenTabs, new CommitFileTabButton { Vm = Vm }, axis: Axis.Horizontal)
                            with { CrossAxis = CrossAxisAlignment.Stretch },
                    ],
                },
            },
        ],
    };
}

/// <summary>The leftmost, always-present tab: shows the commit metadata. Not closable.</summary>
internal sealed record CommitDetailsTab : Widget
{
    public required CommitDetailsViewModel Vm { get; init; }

    protected override IWidget Build(Context ctx) => new CommitTabChrome
    {
        Label = L.T(s => s.CommitsDetailsTab),
        IsActive = () => Vm.SelectedPath.Value == null,
        OnActivate = () => Vm.ActivateTab(null),
    };
}

/// <summary>One open file's tab. Resolves its <see cref="CommitFileTab"/> from the list scope.</summary>
internal sealed record CommitFileTabButton : Widget
{
    public required CommitDetailsViewModel Vm { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var tab = ctx.Require<CommitFileTab>();
        // Present only in a review window; elsewhere null ⇒ the tab shows no Viewed mark.
        var reviewed = ctx.Get<IReviewedFileTracker>();
        return new CommitTabChrome
        {
            Label = tab.FileName,
            IsActive = () => Vm.SelectedPath.Value == tab.Path,
            OnActivate = () => Vm.ActivateTab(tab.Path),
            OnClose = () => Vm.CloseTab(tab.Path),
            Viewed = reviewed == null
                ? null
                : () =>
                {
                    _ = reviewed.Revision.Value;
                    return reviewed.IsViewed(tab.Sha, tab.Path);
                },
        };
    }
}

/// <summary>
/// Shared tab pill: a label that ellipsizes when long, an optional close button, the row-selection
/// fill when active and the hover fill on hover. Composed by both the Details tab and the file tabs.
/// </summary>
internal sealed record CommitTabChrome : Widget
{
    // Tabs shrink to their content, capped here: a longer name ellipsizes, a shorter one stays snug.
    private const float MaxTabWidth = 220f;

    public required Prop<string?> Label { get; init; }
    public required Func<bool> IsActive { get; init; }
    public required Action OnActivate { get; init; }
    public Action? OnClose { get; init; }

    // Optional per-file "Viewed" predicate (review window only). When it returns true a leading
    // success-tinted check sits before the label; null ⇒ no mark (Details tab, History pane).
    public Func<bool>? Viewed { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var input = ctx.Require<InputSystem>();
        var theme = ctx.Theme();
        var hover = new State<bool>(false);

        // The label grows so it ellipsizes into whatever width the (capped) tab leaves it. A flex
        // container measures its intrinsic width from children's *unclamped* natural widths but lays
        // them out clamped, so capping the tab with MaxWidth alone would size the pill to the full name
        // yet clamp the label, leaving dead space. With the label in a Grow, the cap on the pill flows
        // down: a long name shrinks the Grow slot and ellipsizes; a short name leaves the pill snug.
        var label = new Text
        {
            Value = Label,
            FontSize = FontSize.Body,
            VAlign = TextAlignment.Center,
            Overflow = TextOverflow.Ellipsis,
            Color = Prop.Bind(() => IsActive()
                ? theme.Styles.Value.Palette.TextPrimary
                : theme.Styles.Value.Palette.TextSecondary),
        };

        IWidget grow = new Grow { Child = label };
        var rowChildren = new List<IWidget>();
        if (Viewed is { } viewed)
            rowChildren.Add(new Text
            {
                FontFamily = LucideIcons.FontFamily,
                FontSize = FontSize.Caption,
                Value = LucideIcons.CheckSquare,
                VAlign = TextAlignment.Center,
                Visible = Prop.Bind(viewed),
                Color = Theme.Color(s => s.Status.Success),
            });
        rowChildren.Add(grow);
        if (OnClose is { } close)
            rowChildren.Add(CloseButton(close));

        var pill = new Box
        {
            MaxWidth = MaxTabWidth,
            // A trailing 1px divider between adjacent tabs (and after the last one).
            BorderSize = new BorderSizeStyle { Right = 1 },
            BorderColor = Theme.BorderColor(s => new BorderColorStyle { Right = s.Palette.Border }),
            Background = Prop.Bind(() =>
            {
                var sel = theme.Styles.Value.RowSelection;
                if (IsActive()) return sel.Fill;
                return hover.Value ? sel.FillHover : 0u;
            }),
            Children =
            [
                new Padding
                {
                    Amount = new PaddingStyle { Left = Spacing.Md, Right = Spacing.Sm },
                    Children =
                    [
                        new Row
                        {
                            Gap = Spacing.Sm,
                            CrossAxis = CrossAxisAlignment.Center,
                            Children = rowChildren.ToArray(),
                        },
                    ],
                },
            ],
        };

        return pill.WithController(input, () => new TabClickController(hover, OnActivate));
    }

    private static IWidget CloseButton(Action onClose) => new ButtonWidget
    {
        Style = ButtonStyle.Bare(s => Theme.Color(t => t.Palette.TextMuted)),
        Command = new Command(onClose),
        Children = [new ButtonIcon { Value = LucideIcons.X, FontSize = FontSize.Caption }],
    }.WithTooltip(L.T(s => s.CommonClose)).WithController<KbmController>();
}

// Hover tracking + left-click activation for a tab pill. The close button consumes its own click
// first (bubbling), so pressing it closes the tab without also activating it.
internal sealed class TabClickController : KeyboardMouseController
{
    private readonly State<bool> _hover;
    private readonly Action _onClick;

    public TabClickController(State<bool> hover, Action onClick)
    {
        _hover = hover;
        _onClick = onClick;
    }

    public override void OnMouseEnter(ref MouseEnterEvent e) => _hover.Value = true;
    public override void OnMouseExit(ref MouseExitEvent e) => _hover.Value = false;

    public override void OnMouseButtonStateChanged(ref MouseButtonEvent e)
    {
        if (e.Phase != EventPhase.Bubbling) return;
        if (e.Button != MouseButton.Left || e.State != InputState.Released) return;
        _onClick();
        e.Consume();
    }
}
