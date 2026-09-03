using GitBench.Controls;
using GitBench.Features.LocalChanges;
using GitBench.Features.Repos;
using GitBench.Localization;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.FileBrowser;

/// <summary>
/// The strip above the previewed file: where the reader has been on the leading edge, and one tab
/// per open file after it.
/// </summary>
/// <remarks>
/// The same strip the terminal and the commit details use, so a tab is a tab everywhere in the app.
/// It is always drawn, even with nothing open — the back and forward buttons live in it, and a bar
/// that appeared with the first file would move the whole pane down under the reader as they opened
/// one.
/// </remarks>
internal sealed record FileBrowserTabStrip : Widget
{
    public required FileBrowserViewModel Model { get; init; }

    protected override IWidget Build(Context ctx) => new TabStrip
    {
        Leading = new FileBrowserHistoryButtons { Model = Model },
        Tabs =
        [
            Each.Of(Model.Tabs, new FileBrowserTabButton { Model = Model }, axis: Axis.Horizontal)
                with { CrossAxis = CrossAxisAlignment.Stretch },
        ],
    };

    /// <summary>What the active tab wears: the header bar directly below the strip.</summary>
    internal static uint Content(ThemeStyles s) => s.FileChangesSection.HeaderBackground;
}

/// <summary>Back and forward, in that order and pinned before the tabs.</summary>
internal sealed record FileBrowserHistoryButtons : Widget
{
    public const string BackButtonId = "file-browser-back";
    public const string ForwardButtonId = "file-browser-forward";

    // Centres the buttons in the strip by inset rather than by a Center, whose intrinsic height a
    // stretched leading slot has nothing to lay out against.
    private const int Inset = ((int)TabStrip.Height - (int)LocalChangesHeaderActionButton.ButtonSize) / 2;

    public required FileBrowserViewModel Model { get; init; }

    protected override IWidget Build(Context ctx) => new Padding
    {
        Amount = new PaddingStyle
        {
            Left = Spacing.Sm,
            Right = Spacing.Xs,
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
                        Command = new Command(Model.GoBack, Model.CanGoBack),
                    },
                    new LocalChangesHeaderActionButton
                    {
                        Id = ForwardButtonId,
                        Icon = LucideIcons.ChevronRight,
                        Tooltip = L.T(s => s.FileBrowserForward),
                        Command = new Command(Model.GoForward, Model.CanGoForward),
                    },
                ],
            },
        ],
    };
}

/// <summary>One open file's tab. Resolves its <see cref="FileBrowserTab"/> from the list scope.</summary>
internal sealed record FileBrowserTabButton : Widget
{
    public required FileBrowserViewModel Model { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var tab = ctx.Require<FileBrowserTab>();
        var browser = Model;
        var loc = ctx.Localization();

        return new TabChrome
        {
            // Tracked: the qualifier follows whichever tabs currently share this file's name, and
            // the label is in whatever language is current.
            Label = Prop.Bind<string?>(() =>
                FileBrowserTabLabels.For(loc.Strings.Value, browser.Tabs, tab)),
            // Italic while the tab is only borrowed, the way every editor says the same thing: the
            // next file selected in the tree takes this slot rather than opening beside it.
            LabelFontFamily = Prop.Bind(() => tab.Transient.Value ? UiFonts.Italic : string.Empty),
            ContentBackground = FileBrowserTabStrip.Content,
            IsActive = () => ReferenceEquals(browser.ActiveTab.Value, tab),
            OnActivate = () => browser.ActivateTab(tab),
            OnClose = () => browser.CloseTab(tab),
            OnContextMenu = point => RepoBarContextMenu.Show(
                ctx, point, MenuItems(loc.Strings.Value, browser, tab)),
        };
    }

    /// <summary>Built on each opening rather than once, so "close the others" is offered only while
    /// there are others and the labels are in whatever language is current.</summary>
    static IReadOnlyList<RepoBarContextMenu.Item> MenuItems(
        Strings s, FileBrowserViewModel browser, FileBrowserTab tab) =>
    [
        new(s.CommonClose, () => browser.CloseTab(tab), LucideIcons.X),
        new(s.FileBrowserCloseOtherTabs, () => browser.CloseOtherTabs(tab),
            Enabled: browser.Tabs.Count > 1),
        new(s.FileBrowserCloseAllTabs, browser.CloseAllTabs),
    ];
}
