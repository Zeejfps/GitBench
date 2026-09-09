using GitBench.Controls;
using GitBench.Features.FileBrowser;
using GitBench.Features.LocalChanges;
using GitBench.Features.Terminal;
using GitBench.Localization;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.App;

/// <summary>
/// The strip across the top of the content panel: where the reader has been on the leading edge,
/// the two views every repository always has, everything they have opened after them, and the one
/// control that opens a shell pinned on the trailing edge.
/// </summary>
/// <remarks>
/// Changes and History are the app's modes — what used to be a segmented switcher up in the toolbar.
/// Putting them in the same run as the shells and the open files is the whole point: a file is not a
/// mode you switch into, it is another thing you have open, and the strip says so by treating it
/// like one. Every tab carries an icon for what it is; the two that cannot be closed are the two
/// without a close button.
/// </remarks>
internal sealed record RepoContentTabs : Widget
{
    /// <summary>
    /// The plane the content panel is drawn on, which the active tab wears so it reads as a notch
    /// cut onto the panel rather than a chip sitting over it.
    /// </summary>
    internal static uint Content(ThemeStyles s) => s.Palette.Surface;

    protected override IWidget Build(Context ctx)
    {
        var terminals = ctx.Require<ITerminalSessionStore>();
        return new Switch<FileBrowserViewModel?>
        {
            Value = ctx.Require<IFileBrowserStore>().Active,
            Case = browser => new Switch<TerminalTabs?>
            {
                Value = terminals.Tabs,
                Case = shells => new RepoContentTabRun { Browser = browser, Shells = shells },
            },
        };
    }
}

/// <summary>One repository's run of tabs. Rebuilt on a repo switch, because the open files and the
/// running shells are that repository's, and so is the history the back button walks.</summary>
internal sealed record RepoContentTabRun : Widget
{
    public FileBrowserViewModel? Browser { get; init; }
    public TerminalTabs? Shells { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var mode = ctx.Require<State<MainViewMode>>();
        var navigator = ctx.Require<IContentNavigator>();
        var drag = ctx.Require<TabDrag>();
        var run = new ContentTabRun(Shells?.Terminals, Browser?.Tabs);
        var mounted = drag.Bind(run);

        var strip = new TabStrip
        {
            Leading = new ContentHistoryButtons(),
            Tabs =
            [
                new ContentModeTab
                {
                    Mode = mode,
                    Value = MainViewMode.LocalChanges,
                    Icon = LucideIcons.FilePenLine,
                    Label = L.T(s => s.AppModeChanges),
                },
                new ContentModeTab
                {
                    Mode = mode,
                    Value = MainViewMode.History,
                    Icon = LucideIcons.ScrollText,
                    Label = L.T(s => s.AppModeHistory),
                },
                Each.Of(
                    run.Tabs,
                    new ContentTabButton { Browser = Browser, Shells = Shells, Mode = mode, Drag = drag },
                    axis: Axis.Horizontal) with { CrossAxis = CrossAxisAlignment.Stretch },
            ],
            Trailing = Shells is null
                ? null
                : new NewTerminalButton
                {
                    OnShow = shell => navigator.Show(new ContentPlace.Shell(shell)),
                },
        };

        return strip.Use(_ => mounted);
    }
}

/// <summary>One opened tab, whichever kind it is. Resolves it from the list scope and hands it to
/// the widget that knows what a tab of that kind does.</summary>
internal sealed record ContentTabButton : Widget
{
    public FileBrowserViewModel? Browser { get; init; }
    public TerminalTabs? Shells { get; init; }
    public required State<MainViewMode> Mode { get; init; }
    public required TabDrag Drag { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var tab = ctx.Require<ContentTab>();
        var handle = Drag.Handle(tab);

        return tab switch
        {
            ContentTab.Shell shell when Shells is { } shells => new TerminalTab
            {
                Tabs = shells,
                Instance = shell.Instance,
                Drag = handle,
                PaneShowing = () => Mode.Value == MainViewMode.Terminal,
                OnShow = () =>
                    ctx.Require<IContentNavigator>().Show(new ContentPlace.Shell(shell.Instance)),
            },
            ContentTab.File file when Browser is { } browser => new FileBrowserTabButton
            {
                Model = browser,
                Tab = file.Tab,
                Drag = handle,
                PaneShowing = () => Mode.Value == MainViewMode.Files,
            },
            _ => Empty.Widget,
        };
    }
}

/// <summary>One of the two views a repository always has. Never closable, and marked with an icon
/// the way every tab beside it is.</summary>
internal sealed record ContentModeTab : Widget
{
    public required State<MainViewMode> Mode { get; init; }
    public required MainViewMode Value { get; init; }
    public required string Icon { get; init; }
    public required Prop<string?> Label { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var navigator = ctx.Require<IContentNavigator>();
        return new TabChrome
        {
            Leading = new TabIcon
            {
                Glyph = Icon,
                Color = Theme.Color(s => IsActive() ? s.Palette.TextPrimary : s.Palette.TextMuted),
            },
            Label = Label,
            ContentBackground = RepoContentTabs.Content,
            IsActive = IsActive,
            OnActivate = () => navigator.Show(new ContentPlace.View(Value)),
        };
    }

    private bool IsActive() => Mode.Value == Value;
}

/// <summary>
/// Back and forward, in that order and pinned before the tabs.
/// </summary>
/// <remarks>
/// Always there, whatever is open. They walk the panel's whole trail — the two views and the shells
/// as well as the files — so a repository with nothing open still has somewhere to go back to.
/// </remarks>
internal sealed record ContentHistoryButtons : Widget
{
    public const string BackButtonId = "content-back";
    public const string ForwardButtonId = "content-forward";

    // Centres the buttons in the strip by inset rather than by a Center, whose intrinsic height a
    // stretched leading slot has nothing to lay out against.
    private const int Inset = ((int)TabStrip.Height - (int)LocalChangesHeaderActionButton.ButtonSize) / 2;

    protected override IWidget Build(Context ctx)
    {
        var navigator = ctx.Require<IContentNavigator>();

        return new Padding
        {
            // Even on both sides, so the buttons sit centred in the segment the strip's divider
            // closes rather than pushed against it.
            Amount = new PaddingStyle
            {
                Left = Spacing.Sm,
                Right = Spacing.Sm,
                Top = Inset,
                Bottom = Inset,
            },
            Children =
            [
                new Row
                {
                    Gap = Spacing.Xs,
                    CrossAxis = CrossAxisAlignment.Center,
                    Children =
                    [
                        new LocalChangesHeaderActionButton
                        {
                            Id = BackButtonId,
                            Icon = LucideIcons.ChevronLeft,
                            Tooltip = L.T(s => s.FileBrowserBack),
                            Command = new Command(navigator.GoBack, navigator.CanGoBack),
                        },
                        new LocalChangesHeaderActionButton
                        {
                            Id = ForwardButtonId,
                            Icon = LucideIcons.ChevronRight,
                            Tooltip = L.T(s => s.FileBrowserForward),
                            Command = new Command(navigator.GoForward, navigator.CanGoForward),
                        },
                    ],
                },
            ],
        };
    }
}

/// <summary>
/// The mark before a tab's name, saying what kind of thing the tab is.
/// </summary>
/// <remarks>
/// A fixed column rather than the glyph's own advance, the way the tree's icons have one: the two
/// icon fonts are set at different sizes, and a tab whose label started wherever its glyph happened
/// to end would leave the run ragged.
/// </remarks>
internal sealed record TabIcon : Widget
{
    private const float ColumnWidth = 16f;

    public required string Glyph { get; init; }
    public required Prop<uint> Color { get; init; }

    /// <summary>The font the glyph is in. Lucide unless the caller has a mark from another set.</summary>
    public string Family { get; init; } = LucideIcons.FontFamily;

    protected override IWidget Build(Context ctx) => new Box
    {
        Width = ColumnWidth,
        Children =
        [
            new Text
            {
                Value = Glyph,
                FontFamily = Family,
                FontSize = FileGlyph.SizeOf(Family),
                HAlign = TextAlignment.Center,
                VAlign = TextAlignment.Center,
                Color = Color,
            },
        ],
    };
}
