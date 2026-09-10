using GitBench.Controls;
using GitBench.Features.Diff;
using GitBench.Features.Markdown.Parsing;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Markdown.Rendering;

/// <summary>
/// One markdown image drawn as a picture. Loads through <see cref="IMarkdownImageLoader"/> (with
/// the surface's <see cref="IMarkdownImageSource"/> for relative paths), occupies no space until
/// the frame arrives, then shows it at its pixel size shrunk to the available width. When the
/// image cannot load — or no loader is registered — it falls back to the alt text as a link, the
/// same rendering an image inside a text paragraph gets.
/// </summary>
internal sealed record MarkdownImage : Widget
{
    /// <summary>The image run; <see cref="InlineRun.ImageSrc"/> must be set.</summary>
    public required InlineRun Run { get; init; }

    /// <summary>The paragraph text color of the enclosing scope, for the alt-text fallback.</summary>
    public required Func<ThemeStyles, uint> BodyColor { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var loader = ctx.Get<IMarkdownImageLoader>();
        if (loader is null || Run.ImageSrc is not { } src) return Fallback();

        var source = ctx.Get<IMarkdownImageSource>();
        var failed = new State<bool>(false);
        return new Show
        {
            When = failed,
            Then = Fallback,
            Else = () => new Raw
            {
                View = new MarkdownImageView(loader, source, src, () => failed.Value = true),
            },
        };
    }

    private IWidget Fallback() => MarkdownWidget.InlineText(new[] { Run }, FontSize.Body, bold: false, BodyColor);
}

/// <summary>
/// The paragraph shapes that draw as pictures rather than text: every run is an image, a hard
/// break, or whitespace. Images on one line sit in a row; hard breaks start a new row.
/// </summary>
internal static class MarkdownImageParagraph
{
    public static bool IsImageOnly(IReadOnlyList<InlineRun> runs)
    {
        var any = false;
        foreach (var run in runs)
        {
            if (run.ImageSrc != null) any = true;
            else if (run.Text != "\n" && (run.Code || !string.IsNullOrWhiteSpace(run.Text))) return false;
        }
        return any;
    }

    /// <summary>The image runs of each line, in order; lines without an image are dropped.</summary>
    public static List<List<InlineRun>> Lines(IReadOnlyList<InlineRun> runs)
    {
        var lines = new List<List<InlineRun>>();
        var line = new List<InlineRun>();
        foreach (var run in runs)
        {
            if (run.ImageSrc != null) line.Add(run);
            else if (run.Text == "\n" && line.Count > 0)
            {
                lines.Add(line);
                line = new List<InlineRun>();
            }
        }
        if (line.Count > 0) lines.Add(line);
        return lines;
    }
}

/// <summary>
/// The view behind <see cref="MarkdownImage"/>: starts its load when mounted, cancels it when
/// unmounted, and owns the one canvas texture it uploads. Measures 0×0 until the frame lands.
/// </summary>
internal sealed class MarkdownImageView : View
{
    private static int _nextId;

    private readonly string _imageId = $"markdown-image:{Interlocked.Increment(ref _nextId)}";
    private readonly IMarkdownImageLoader _loader;
    private readonly IMarkdownImageSource? _source;
    private readonly string _src;
    private readonly Action _onFailed;

    private ImageFrame? _frame;
    private IDisposable? _load;
    private ICanvas? _canvas;
    private bool _uploaded;

    public MarkdownImageView(IMarkdownImageLoader loader, IMarkdownImageSource? source, string src, Action onFailed)
    {
        _loader = loader;
        _source = source;
        _src = src;
        _onFailed = onFailed;
        Behaviors.Add(new Lifetime());
    }

    /// <summary>The decoded frame, once it has arrived.</summary>
    internal ImageFrame? Frame => _frame;

    /// <summary>
    /// The drawn size of a <paramref name="frameWidth"/>×<paramref name="frameHeight"/> picture in
    /// <paramref name="availableWidth"/>: its pixel size, shrunk proportionally when wider than the
    /// space, never magnified. A non-positive width means unconstrained.
    /// </summary>
    public static (float Width, float Height) Fit(int frameWidth, int frameHeight, float availableWidth)
    {
        if (frameWidth <= 0 || frameHeight <= 0) return (0f, 0f);
        var width = availableWidth > 0f ? MathF.Min(frameWidth, availableWidth) : frameWidth;
        var height = width * frameHeight / frameWidth;
        return (MathF.Round(width), MathF.Max(1f, MathF.Round(height)));
    }

    protected override float MeasureWidthIntrinsic()
    {
        if (Width.IsSet) return Width;
        return _frame?.Width ?? 0f;
    }

    protected override float MeasureHeightIntrinsic(float availableWidth)
    {
        if (Height.IsSet) return Height;
        if (_frame is null) return 0f;
        return Fit(_frame.Width, _frame.Height, availableWidth).Height;
    }

    protected override void OnDrawSelf(ICanvas c)
    {
        _canvas = c;
        if (_frame is not { } frame) return;
        if (!_uploaded)
            _uploaded = c.CreateOrUpdateRgbaImage(_imageId, frame.Width, frame.Height, frame.Rgba);
        if (!_uploaded) return;

        var bounds = Position;
        var (width, height) = Fit(frame.Width, frame.Height, bounds.Width);
        if (width < 1f || height < 1f) return;
        c.DrawImage(new DrawImageInputs
        {
            Position = new RectF(bounds.Left, bounds.Bottom + bounds.Height - height, width, height),
            ImageId = _imageId,
            ZIndex = GetDrawZIndex(),
            TintColor = 0xFFFFFFFF,
            Rotation = 0f,
        });
    }

    private void Start()
    {
        _load ??= _loader.Load(_src, _source, OnLoaded);
    }

    private void OnLoaded(ImageFrame? frame)
    {
        if (frame is null)
        {
            _onFailed();
            return;
        }
        _frame = frame;
        _uploaded = false;
        SetDirty();
    }

    private void Stop()
    {
        _load?.Dispose();
        _load = null;
        if (_uploaded) _canvas?.RemoveImage(_imageId);
        _uploaded = false;
    }

    private sealed class Lifetime : IViewBehavior
    {
        public void Attach(View view) => ((MarkdownImageView)view).Start();
        public void Detach(View view) => ((MarkdownImageView)view).Stop();
    }
}
