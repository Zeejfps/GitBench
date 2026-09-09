using GitBench.App;
using GitBench.Controls;
using GitBench.Features.LanguageServers;
using GitBench.Features.LocalChanges;
using GitBench.Features.Repos;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.FileBrowser;

/// <summary>
/// The content panel's file tab: the working tree as it is on disk, not as git sees it.
/// </summary>
/// <remarks>
/// The three tabs beside this one are all views of git — changed files, committed files, reviewed
/// files — and none of them answers "what is actually in this file". The tree that opens these is in
/// the sidebar; this is only the file it opened. The pane follows
/// <see cref="IFileBrowserStore.Active"/> rather than reading the registry itself, so switching
/// repositories swaps which file is on screen while the others keep theirs.
/// </remarks>
internal sealed record FileBrowserPane : Widget
{
    protected override IWidget Build(Context ctx) => new Switch<FileBrowserViewModel?>
    {
        Value = ctx.Require<IFileBrowserStore>().Active,
        Case = browser => browser is null
            ? new FileBrowserNotice { Message = L.T(s => s.FileBrowserNoRepo) }
            : new FileBrowserBody { Model = browser },
    };
}

/// <summary>One repository's open file: the path bar over it, and the file itself.</summary>
internal sealed record FileBrowserBody : Widget
{
    public required FileBrowserViewModel Model { get; init; }

    protected override IWidget Build(Context ctx) => new Column
    {
        CrossAxis = CrossAxisAlignment.Stretch,
        Children =
        [
            new FileBrowserPreviewHeader { Model = Model },
            new Grow { Child = new FileBrowserPreview { Model = Model } },
        ],
    };
}

/// <summary>The preview's header: which file is on screen, which declaration the reader is inside
/// of it, and the one control that changes how it is drawn.</summary>
internal sealed record FileBrowserPreviewHeader : Widget
{
    public required FileBrowserViewModel Model { get; init; }

    protected override View CreateView(Context ctx)
    {
        var browser = Model;

        var title = new TextView(ctx.Canvas) { TextOverflow = TextOverflow.Ellipsis };
        title.BindThemedTextColor(ctx.Theme(), s => s.FileChangesSection.HeaderText);
        title.Bind(browser.Preview, preview => title.Text = browser.TitleFor(preview));

        // Second, dimmer, and after the path rather than replacing it: the path says which file,
        // and the breadcrumb only ever says where in it.
        var breadcrumb = new TextView(ctx.Canvas) { TextOverflow = TextOverflow.Ellipsis };
        breadcrumb.BindThemedTextColor(ctx.Theme(), s => s.Palette.TextMuted);
        breadcrumb.Bind(browser.Breadcrumb, path => breadcrumb.Text = path is null ? string.Empty : Separator + path);

        var servers = new LanguageServerStatusChip { Model = browser }.BuildView(ctx);

        var find = new LocalChangesHeaderActionButton
        {
            Icon = LucideIcons.Search,
            Visible = Prop.Bind(() => browser.CanSearch),
            Tooltip = L.T(s => s.FileSearchTitle),
            Command = new Command(browser.Search.Open),
        }.BuildView(ctx);

        var toggle = new LocalChangesHeaderActionButton
        {
            Icon = Prop.Bind<string?>(() =>
                browser.RenderMarkdown.Value ? LucideIcons.FileText : LucideIcons.BookOpen),
            Visible = Prop.Bind(() => browser.MarkdownPreview != null),
            Tooltip = L.T(s => s.DiffPreviewToggleTooltip),
            Command = new Command(() => browser.SetRenderMarkdown(!browser.RenderMarkdown.Value)),
        }.BuildView(ctx);

        return FileChangesUI.CreateHeaderBar(ctx, new FlexRowView
        {
            CrossAxisAlignment = CrossAxisAlignment.Center,
            MinHeightConstraint = LocalChangesHeaderActionButton.ButtonSize,
            Children =
            {
                new FlexItem { Shrink = 1, Child = title },
                new FlexItem { Grow = 1, Shrink = 2, Child = breadcrumb },
                servers,
                find,
                toggle,
            },
        }, topBorder: false, background: RepoContentTabs.Content);
    }

    // U+203A, not a chevron glyph: the header's text runs are in the UI font, and a lucide glyph
    // here would need its own view just to carry a different family.
    private const string Separator = " › ";
}

/// <summary>The tree, where the branches would be. Follows the active repository the way the pane
/// on the other side of the window does.</summary>
internal sealed record FileBrowserTreePane : Widget
{
    protected override IWidget Build(Context ctx) => new Box
    {
        Background = Theme.Color(s => s.Palette.Surface),
        Children =
        [
            new Switch<FileBrowserViewModel?>
            {
                Value = ctx.Require<IFileBrowserStore>().Active,
                Case = browser => browser is null
                    ? new FileBrowserNotice { Message = L.T(s => s.FileBrowserNoRepo) }
                    : new FileBrowserTreeColumn { Model = browser },
            },
        ],
    };
}

/// <summary>The rail's list, with the find field over it while there is one.</summary>
internal sealed record FileBrowserTreeColumn : Widget
{
    public required FileBrowserViewModel Model { get; init; }

    protected override IWidget Build(Context ctx) => new Column
    {
        CrossAxis = CrossAxisAlignment.Stretch,
        Children =
        [
            // Mounted rather than merely shown, so opening the finder is what gives the field the
            // caret and closing it hands the caret back to the tree.
            new Show
            {
                When = Model.Finder.IsOpen,
                Then = () => new FileFinderField { Model = Model },
            },
            new Grow { Child = new FileBrowserTreeRail { Model = Model } },
        ],
    };
}

/// <summary>Widget wrapper so the virtualized tree composes into the rail like any other child.</summary>
internal sealed record FileBrowserTreeRail : Widget
{
    public required FileBrowserViewModel Model { get; init; }

    protected override View CreateView(Context ctx)
    {
        var browser = Model;
        var tree = new FileBrowserTreeView(ctx, browser);
        var menu = new FileBrowserContextMenu(ctx);

        tree.RowContextRequested += (row, at) =>
        {
            var items = menu.Build(browser, row);
            if (items.Count == 0)
            {
                tree.ClearContextHighlight();
                return;
            }

            var opened = RepoBarContextMenu.Show(ctx, at, items);
            if (opened is null) tree.ClearContextHighlight();
            else opened.Closed += tree.ClearContextHighlight;
        };

        return tree;
    }
}

/// <summary>A line of text where the browser would be, on the browser's own background.</summary>
internal sealed record FileBrowserNotice : Widget
{
    public Prop<string?> Message { get; init; }

    protected override IWidget Build(Context ctx) => new Box
    {
        Background = Theme.Color(s => s.Palette.Surface),
        Children =
        [
            new Center
            {
                Child = new Text
                {
                    Value = Message,
                    Color = Theme.Color(s => s.Palette.TextSecondary),
                },
            },
        ],
    };
}
