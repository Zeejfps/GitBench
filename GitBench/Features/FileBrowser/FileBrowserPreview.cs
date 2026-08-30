using GitBench.Controls;
using GitBench.Features.Diff;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.FileBrowser;

/// <summary>Which body the preview shows. Text is the common case; a picture and a sentence are the
/// two things a patch view cannot render.</summary>
internal enum FileBrowserBodyKind { Text, Image, Placeholder }

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
                        FilePreview.Text => FileBrowserBodyKind.Text,
                        FilePreview.Image => FileBrowserBodyKind.Image,
                        _ => FileBrowserBodyKind.Placeholder,
                    }),
                    KeepAlive = true,
                    Case = kind => kind switch
                    {
                        FileBrowserBodyKind.Text => new FileBrowserTextBody { Model = browser },
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

        content.Bind(browser.Preview, preview =>
        {
            if (preview is FilePreview.Text text) content.SetRenderState(ToRenderState(text));
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
            Highlight: text.Highlight);

    private static readonly IReadOnlySet<int> EmptyLineNumbers = new HashSet<int>();
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
