using GitBench.App;
using GitBench.Controls;
using GitBench.Features.Diff;
using GitBench.Features.Editor;
using GitBench.Features.LanguageServers;
using GitBench.Features.Markdown;
using GitBench.Features.Markdown.Parsing;
using GitBench.Features.Markdown.Rendering;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.FileBrowser;

/// <summary>
/// The pane beside the tree: the selected file, rendered.
/// </summary>
/// <remarks>
/// <para>
/// Text goes through <see cref="DiffContentView"/> in whole-file mode — a single line-number gutter
/// and per-line syntax spans, which is a text viewer that happens to live in the diff namespace.
/// The diff body does not render pictures (<c>DiffRowSet.Build</c> flattens only the two text
/// states), so an image takes its own body here, composed from the view-model-free
/// <see cref="ImagePreviewSurface"/> rather than from <c>ImagePreviewView</c>, which requires a
/// <c>DiffViewModel</c> this pane does not have.
/// </para>
/// <para>
/// <see cref="DiffContentView.AssistantActions"/> stays false. It defaults false and gates the only
/// route into the assistant, so this is belt and braces: selecting and copying still work, and a
/// human pasting into the composer is a decision, not a bypass.
/// </para>
/// </remarks>
internal sealed record FileBrowserPreview : Widget
{
    public required FileBrowserViewModel Model { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var browser = Model;

        return new Box
        {
            Background = Theme.Color(s => s.DiffView.PanelBackground),
            Children =
            [
                new Switch<FileBrowserBodyKind>
                {
                    Value = new Derived<FileBrowserBodyKind>(() => browser.BodyKind),
                    KeepAlive = true,
                    Case = kind => kind switch
                    {
                        FileBrowserBodyKind.Text => new FileBrowserTextBody { Model = browser },
                        FileBrowserBodyKind.Markdown => new FileBrowserMarkdownBody { Model = browser },
                        FileBrowserBodyKind.Image => new FileBrowserImageBody { Model = browser },
                        _ => new FileBrowserNotice { Message = Prop.Bind<string?>(() => Placeholder(ctx, browser)) },
                    },
                },
            ],
        };
    }

    private static string Placeholder(Context ctx, FileBrowserViewModel browser)
    {
        var s = ctx.Localization().Strings.Value;
        return browser.Preview.Value switch
        {
            FilePreview.Loading => s.CommonLoading,
            FilePreview.Unavailable u => u.Reason switch
            {
                FilePreviewRefusal.Binary => s.FileBrowserPreviewBinary,
                FilePreviewRefusal.TooLarge => s.FileBrowserPreviewTooLarge,
                FilePreviewRefusal.Missing => s.FileBrowserPreviewMissing,
                _ => s.FileBrowserPreviewUnreadable,
            },
            _ => s.FileBrowserPreviewNone,
        };
    }
}

/// <summary>The file's text, in the whole-file viewer.</summary>
internal sealed record FileBrowserTextBody : Widget
{
    public required FileBrowserViewModel Model { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var browser = Model;
        var content = new DiffContentView(ctx);
        var vScrollBar = ScrollBars.CreateVertical(ctx);
        var hScrollBar = ScrollBars.CreateHorizontal(ctx);
        hScrollBar.IsRtl = false;
        content.Use(() => new ScrollSyncController(content, vScrollBar, hScrollBar));

        // Showing a file is what starts its language server, not asking it a question. The wait is
        // tens of seconds on a cold project, and it should be spent while the file is being read.
        var languageServers = ctx.Get<ILanguageServerStore>();

        content.Bind(browser.Preview, preview =>
        {
            if (preview is not FilePreview.Text text) return;
            content.SetRenderState(ToRenderState(text), Editable(browser, text));
            // Recomputed rather than emptied. Saving a file re-reads it, which lands here for the
            // file already on screen, and a server only speaks when it has something new to say —
            // so blanking here drops what it already said until it happens to say it again.
            content.SetDiagnostics(OverlayFor(languageServers, text.Path));
            languageServers?.FileShown(text.Path);
        });

        if (languageServers is not null)
            content.Bind(languageServers.Diagnostics, _ => content.SetDiagnostics(
                browser.Preview.Value is FilePreview.Text shown
                    ? OverlayFor(languageServers, shown.Path)
                    : DiffDiagnosticOverlay.Empty));
        // The find bar's hits, and the reveal that follows them. One slice carries both the list and
        // the cursor, so a step and a re-scan arrive here as the same kind of event.
        content.Bind(browser.Search.Hits, hits =>
        {
            content.SetSearch(new DiffSearchOverlay(hits));
            if (hits.At is { } at) content.RevealSearchMatch(at);
        });

        // After the render state, and on its own path: a fold toggle must not run the render-state
        // transition, which would reset horizontal scroll and restore a stale pixel offset.
        content.Bind(browser.Folds, content.SetFoldState);

        // Asking a language server about whatever the pointer rests on. Only here: the diff pane and
        // the review window show a file as it was at a commit, and a server asked about that would
        // answer about the file on disk instead.
        Func<(string Root, string Path)?> document = () => browser.Preview.Value is FilePreview.Text text
            ? (browser.RootPath, text.Path)
            : null;

        if (languageServers is { } servers && ctx.Get<HoverPopupService>() is { } hovers)
        {
            content.UseController(ctx.Require<InputSystem>(), () => new HoverProbeController(
                content,
                servers,
                hovers,
                ctx.Require<IUiDispatcher>(),
                document));
        }

        if (languageServers is { } definitions)
        {
            var input = ctx.Require<InputSystem>();
            var usages = new UsagesPopup(
                ctx, definitions, browser, ctx.Require<IUiDispatcher>(), document,
                ctx.Require<IFileTextSource>());
            content.Use(() => usages);

            content.UseController(input, () => new DefinitionProbeController(
                content,
                definitions,
                browser,
                ctx.Require<IContentNavigator>(),
                ctx.Require<IUiDispatcher>(),
                document,
                () => input.Modifiers,
                usages: usages), EventPhaseFilter.Capture);

            // A lens click asks the same question Shift+F12 does, about the declaration's own name.
            content.Use(() =>
            {
                var subscriptions = new SubscriptionGroup();
                Action<UsageLensTarget, PointF> activated = (target, anchor) =>
                    usages.ShowUsagesOf(anchor, target.NameLine, target.NameColumn);
                content.UsageLensActivated += activated;
                subscriptions.Add(() => content.UsageLensActivated -= activated);
                return subscriptions;
            });

            if (DiffOptions.UsageLensEnabled) KeepUsageCountsFilledIn(ctx, content, browser, definitions);
        }

        // Both directions of the header's conversation with the body: a line to reveal on the way
        // in, the line at the top of the viewport on the way back out. Held for the view's mounted
        // period rather than for the browser's, which outlives every body the preview swaps
        // through.
        content.Use(() =>
        {
            var subscriptions = new SubscriptionGroup();
            // Where the browser's plain line numbers meet the body's own FileLine axis.
            Action<int> reveal = line => content.RequestScrollToNewLine(new FileLine(line));
            Action<FileLine?> publishTop = line => browser.SetTopVisibleLine(line?.Value ?? 0);
            browser.LineRevealRequested += reveal;
            subscriptions.Add(() => browser.LineRevealRequested -= reveal);
            content.TopVisibleLineChanged += publishTop;
            subscriptions.Add(() => content.TopVisibleLineChanged -= publishTop);
            content.OnToggleFold += browser.ToggleFold;
            subscriptions.Add(() => content.OnToggleFold -= browser.ToggleFold);
            return subscriptions;
        });

        return new BorderLayout
        {
            // Over the text and not over the scrollbars: the bar floats within the file it is
            // searching, the way it does in an editor.
            Center = new Stack
            {
                Children =
                [
                    new Raw { View = content },
                    new Show
                    {
                        When = browser.Search.IsOpen,
                        Then = () => new FileSearchBarPlacement { Model = browser.Search },
                    },
                ],
            },
            East = new Raw { View = vScrollBar },
            South = new Raw { View = hScrollBar },
        };
    }

    /// <summary>
    /// Keeps the usages rows answered for as long as this body is mounted. The coordinator is asked
    /// to reconsider on a new file, a scroll and a fold alike: all three change which declarations
    /// are on screen, and which are on screen is the whole of what decides what is worth asking.
    /// </summary>
    /// <remarks>
    /// This is the one surface the rows appear on. The diff pane and the review window show a file
    /// as it was at a commit, and a server asked about that answers about the file on disk.
    /// </remarks>
    private static void KeepUsageCountsFilledIn(
        Context ctx, DiffContentView content, FileBrowserViewModel browser, ILanguageServerStore servers) =>
        content.Use(() =>
        {
            var counts = new UsageLensCoordinator(
                servers,
                ctx.Require<IUiDispatcher>(),
                () => (browser.Preview.Value as FilePreview.Text)?.Path,
                content.VisibleUsageLensTargets,
                content.UsageLensTargets,
                rows => content.UsageLensRows = rows,
                content.SetUsageLens);

            var subscriptions = new SubscriptionGroup();
            Action<float> scrolled = _ => counts.Refresh();
            content.VerticalScrollPositionChanged += scrolled;
            subscriptions.Add(() => content.VerticalScrollPositionChanged -= scrolled);
            subscriptions.Add(browser.Preview.Subscribe(_ => counts.Refresh()));
            subscriptions.Add(browser.Folds.Subscribe(_ => counts.Refresh()));
            // The two things a server does that mean it now knows more about this file than it did
            // when it last answered: it changed state (started, finished loading, failed), or it
            // published a fresh wave of diagnostics, which it only does once it has analysed the
            // project the file sits in. Both are worth asking again on; Recheck bounds how often.
            subscriptions.Add(servers.Active.Subscribe(_ => counts.Recheck()));
            subscriptions.Add(servers.Diagnostics.Subscribe(_ => counts.Recheck()));
            subscriptions.Add(counts);
            return subscriptions;
        });

    /// <summary>What the servers currently say about one path, or nothing where they are talking
    /// about a different file — or where there are no servers at all.</summary>
    private static DiffDiagnosticOverlay OverlayFor(ILanguageServerStore? servers, string path)
    {
        if (servers?.Diagnostics.Value is not { } diagnostics || !diagnostics.IsFor(path))
            return DiffDiagnosticOverlay.Empty;
        return new DiffDiagnosticOverlay(diagnostics.Path, diagnostics.Items);
    }

    /// <summary>The same file as something the reader can put a caret in, or null where it must
    /// stay a viewer.</summary>
    private static EditorBuffer? Editable(FileBrowserViewModel browser, FilePreview.Text text) =>
        browser.Documents.Open(text.Path, text.Lines, text.WriteBack, text.Highlight);

    private static DiffRenderState.FullFile ToRenderState(FilePreview.Text text) =>
        new(
            text.Path,
            text.Lines,
            AddedLineNumbers: EmptyLineNumbers,
            Side: DiffSide.WorkingTree,
            Truncated: text.Truncated,
            Emphasis: null,
            Annotations: text.Highlight is null && text.Outline is null
                ? null
                : new DiffAnnotations(text.Highlight, text.Outline, null));

    private static readonly IReadOnlySet<int> EmptyLineNumbers = new HashSet<int>();
}

internal sealed record FileBrowserMarkdownBody : Widget
{
    public required FileBrowserViewModel Model { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var browser = Model;
        var loc = ctx.Localization();

        return new MarkdownDocumentView
        {
            Document = Prop.Bind<MarkdownDocument?>(() => browser.MarkdownPreview?.Document),
            BottomNotice = Prop.Bind<string?>(() => browser.MarkdownPreview is { Truncated: true }
                ? loc.Strings.Value.DiffFileTruncated(DiffOptions.TruncationLineCap)
                : null),
            ImageSource = Prop.Bind<IMarkdownImageSource?>(() =>
                browser.Preview.Value is FilePreview.Text { Markdown: not null } text
                    ? WorkingTreeImageSource.For(browser.RootPath, text.Path)
                    : null),
        };
    }
}

/// <summary>
/// Relative images of a markdown file in the file browser, read from the working tree under the
/// browser's root. <see cref="For"/> yields null when the file is not under that root, so its
/// relative paths have nothing to resolve against.
/// </summary>
internal sealed record WorkingTreeImageSource(string Root, string BaseDir) : IMarkdownImageSource
{
    public static WorkingTreeImageSource? For(string root, string absoluteFilePath)
    {
        var relative = Path.GetRelativePath(root, absoluteFilePath);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar))
            return null;
        return new WorkingTreeImageSource(root, MarkdownImagePath.DirectoryOf(relative));
    }

    public byte[]? Read(string path, int maxBytes)
    {
        try
        {
            var full = Path.GetFullPath(Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar)));
            var root = Path.GetFullPath(Root);
            if (!full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                return null;
            var info = new FileInfo(full);
            if (!info.Exists || info.Length <= 0 || info.Length > maxBytes) return null;
            return File.ReadAllBytes(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}

/// <summary>The file as a picture.</summary>
internal sealed record FileBrowserImageBody : Widget
{
    public required FileBrowserViewModel Model { get; init; }

    protected override View CreateView(Context ctx)
    {
        var surface = new ImagePreviewSurface(ctx);
        surface.Bind(Model.Preview, preview =>
            surface.SetPreview((preview as FilePreview.Image)?.Preview));
        return surface;
    }
}
