using GitBench.App;
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

/// <summary>One open file's tab. Resolves its <see cref="FileBrowserTab"/> from the list scope.</summary>
internal sealed record FileBrowserTabButton : Widget
{
    public required FileBrowserViewModel Model { get; init; }

    /// <summary>
    /// Whether the content panel is showing a file at all. A file tab shares its strip with the
    /// tabs of three other views, so being the browser's active tab is only half of being the tab
    /// the reader is looking at.
    /// </summary>
    public required Func<bool> PaneShowing { get; init; }

    /// <summary>The file this tab is of. Handed in rather than resolved from the list scope, because
    /// the run these sit in holds tabs of more than one kind and only its own wrapper is in scope.</summary>
    public required FileBrowserTab Tab { get; init; }

    /// <summary>How this tab is dragged to a new place in the run.</summary>
    public required ITabDrag Drag { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var tab = Tab;
        var browser = Model;
        var loc = ctx.Localization();
        var (glyph, family) = FileGlyph.For(tab.Name);

        return new TabChrome
        {
            Drag = Drag,
            // The same mark and the same tint the tree gives this file, so a tab and the row it was
            // opened from are recognisably the same thing.
            Leading = new TabIcon
            {
                Glyph = glyph,
                Family = family,
                Color = Theme.Color(s => s.FileBrowserRow.IconFor(FileKinds.Classify(tab.Name))),
            },
            Mark = new FileBrowserTabUnsavedMark { Model = browser, Tab = tab },
            // Tracked: the qualifier follows whichever tabs currently share this file's name, and
            // the label is in whatever language is current.
            Label = Prop.Bind<string?>(() =>
                FileBrowserTabLabels.For(loc.Strings.Value, browser.Tabs, tab)),
            // Italic while the tab is only borrowed, the way every editor says the same thing: the
            // next file selected in the tree takes this slot rather than opening beside it.
            LabelFontFamily = Prop.Bind(() => tab.Transient.Value ? UiFonts.Italic : string.Empty),
            ContentBackground = RepoContentTabs.Content,
            IsActive = () => PaneShowing() && ReferenceEquals(browser.ActiveTab.Value, tab),
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

/// <summary>The dot a tab wears while its file has edits that are not on disk. Laid out on every
/// tab and painted only where there are edits, so the tab never changes width.</summary>
internal sealed record FileBrowserTabUnsavedMark : Widget
{
    private const float Size = 6f;

    public required FileBrowserViewModel Model { get; init; }
    public required FileBrowserTab Tab { get; init; }

    protected override IWidget Build(Context ctx) => new Box
    {
        Width = Size,
        Height = Size,
        BorderRadius = BorderRadiusStyle.All(Size / 2f),
        Background = Theme.Color(s =>
            Model.Documents.HasUnsavedEdits(Tab.Path) ? s.Palette.TextPrimary : 0u),
    };
}
