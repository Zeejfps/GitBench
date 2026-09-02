using GitBench.Controls;
using GitBench.Features.Diff;
using GitBench.Features.LanguageServers;
using GitBench.Features.Markdown;
using GitBench.Features.Markdown.Parsing;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.FileBrowser;

/// <summary>Which body the preview shows. Text is the common case; a picture and a sentence are the
/// two things a patch view cannot render.</summary>
internal enum FileBrowserBodyKind { Text, Markdown, Image, Placeholder }

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
                    Value = new Derived<FileBrowserBodyKind>(() => browser.Preview.Value switch
                    {
                        FilePreview.Text { Markdown: not null } when browser.RenderMarkdown.Value =>
                            FileBrowserBodyKind.Markdown,
                        FilePreview.Text => FileBrowserBodyKind.Text,
                        FilePreview.Image => FileBrowserBodyKind.Image,
                        _ => FileBrowserBodyKind.Placeholder,
                    }),
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
            content.SetRenderState(ToRenderState(text));
            content.SetDiagnostics(DiffDiagnosticOverlay.Empty);
            languageServers?.FileShown(text.Path);
        });

        if (languageServers is not null)
            content.Bind(languageServers.Diagnostics, diagnostics => content.SetDiagnostics(
                browser.Preview.Value is FilePreview.Text shown && diagnostics.IsFor(shown.Path)
                    ? new DiffDiagnosticOverlay(diagnostics.Path, diagnostics.Items)
                    : DiffDiagnosticOverlay.Empty));
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
            content.UseController(input, () => new DefinitionProbeController(
                content,
                definitions,
                browser,
                ctx.Require<IUiDispatcher>(),
                document,
                () => input.Modifiers), EventPhaseFilter.Capture);
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
            Center = new Raw { View = content },
            East = new Raw { View = vScrollBar },
            South = new Raw { View = hScrollBar },
        };
    }

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
        };
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
