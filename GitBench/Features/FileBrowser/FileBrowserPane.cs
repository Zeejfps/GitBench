using GitBench.App;
using GitBench.Controls;
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
/// The Files mode: the active repository's working tree as it is on disk, not as git sees it.
/// </summary>
/// <remarks>
/// The three modes beside this one are all views of git — changed files, committed files, reviewed
/// files — and none of them answers "what is actually in this directory". So this one lists the
/// filesystem: ignored directories, build output, dotfiles and empty directories included, and only
/// <c>.git</c> left out. The pane follows <see cref="IFileBrowserStore.Active"/> rather than reading
/// the registry itself, so switching repositories swaps which tree is on screen while the others
/// keep their open directories.
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

/// <summary>One repository's browser: the tree rail on the leading edge, the preview beside it.</summary>
internal sealed record FileBrowserBody : Widget
{
    public required FileBrowserViewModel Model { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var preferences = ctx.Require<PreferencesService>();
        var browser = Model;

        return new BorderLayout
        {
            West = new ResizableSidebar
            {
                Content = new Box
                {
                    Background = Theme.Color(s => s.Palette.Surface),
                    Children =
                    [
                        new Column
                        {
                            CrossAxis = CrossAxisAlignment.Stretch,
                            Children =
                            [
                                new FileBrowserHeader { Model = browser },
                                new Grow { Child = new FileBrowserTreeRail { Model = browser } },
                            ],
                        },
                    ],
                },
                InitialWidth = preferences.Current.FileBrowserWidth,
                OnWidthChanged = preferences.SetFileBrowserWidth,
            },
            Center = new Column
            {
                CrossAxis = CrossAxisAlignment.Stretch,
                Children =
                [
                    new FileBrowserPreviewHeader { Model = browser },
                    new Grow { Child = new FileBrowserPreview { Model = browser } },
                ],
            },
        };
    }
}

/// <summary>The rail's header: the working tree's own name, and the one control that changes what
/// the tree lists.</summary>
internal sealed record FileBrowserHeader : Widget
{
    public required FileBrowserViewModel Model { get; init; }

    protected override View CreateView(Context ctx)
    {
        var browser = Model;
        var title = new TextView(ctx.Canvas) { Text = Path.GetFileName(browser.RootPath) };
        title.BindThemedTextColor(ctx.Theme(), s => s.FileChangesSection.HeaderText);

        var toggle = new LocalChangesHeaderActionButton
        {
            Icon = LucideIcons.ListFilter,
            Tooltip = L.T(s => s.FileBrowserShowHidden),
            Command = new Command(() => browser.SetShowHidden(!browser.ShowHidden.Value)),
        }.BuildView(ctx);

        return FileChangesUI.CreateHeaderBar(ctx, new FlexRowView
        {
            CrossAxisAlignment = CrossAxisAlignment.Center,
            Children =
            {
                new FlexItem { Grow = 1, Child = title },
                toggle,
            },
        });
    }
}

internal sealed record FileBrowserPreviewHeader : Widget
{
    public required FileBrowserViewModel Model { get; init; }

    protected override View CreateView(Context ctx)
    {
        var browser = Model;

        var title = new TextView(ctx.Canvas);
        title.BindThemedTextColor(ctx.Theme(), s => s.FileChangesSection.HeaderText);
        title.Bind(browser.Preview, preview => title.Text = Title(browser, preview));

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
                new FlexItem { Grow = 1, Child = title },
                toggle,
            },
        });
    }

    private static string Title(FileBrowserViewModel browser, FilePreview preview)
    {
        var path = preview switch
        {
            FilePreview.Loading loading => loading.Path,
            FilePreview.Text text => text.Path,
            FilePreview.Image image => image.Path,
            FilePreview.Unavailable unavailable => unavailable.Path,
            _ => null,
        };
        return path is null ? string.Empty : Path.GetRelativePath(browser.RootPath, path).Replace('\\', '/');
    }
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
