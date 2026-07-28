using GitBench.Controls;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Diff;

/// <summary>
/// The diff body for an image file: the blob rendered as a picture, with a caption naming its
/// pixel size and weight on disk. There is no image diff — this is the state of the file on the
/// side being viewed, which is what "what does this asset look like now" actually asks for.
/// </summary>
internal sealed record ImagePreviewView : Widget
{
    private const float CaptionHeight = 24f;

    protected override IWidget Build(Context ctx)
    {
        var vm = ctx.Require<DiffViewModel>();
        var loc = ctx.Localization();

        var surface = new ImagePreviewSurface(ctx);
        surface.Bind(vm.RenderState, state =>
            surface.SetPreview((state as DiffRenderState.Image)?.Preview));

        return new Box
        {
            Background = Theme.Color(s => s.DiffView.PanelBackground),
            Children =
            [
                new BorderLayout
                {
                    Center = new Raw { View = surface },
                    South = new Box
                    {
                        Height = CaptionHeight,
                        Children =
                        [
                            new Text
                            {
                                Value = Prop.Bind(() => Caption(vm, loc)),
                                FontSize = FontSize.Caption,
                                HAlign = TextAlignment.Center,
                                VAlign = TextAlignment.Center,
                                Color = Theme.Color(s => s.DiffContent.PlaceholderText),
                            },
                        ],
                    },
                },
            ],
        };
    }

    /// <summary>
    /// The height this body needs for <paramref name="preview"/> at a given width: the picture at its
    /// fitted size — never magnified, never taller than <paramref name="maxImageHeight"/> — plus the
    /// caption. For hosts that lay the body out themselves (the stacked review list) so the slot they
    /// reserve matches what the view puts in it.
    /// </summary>
    internal static float MeasureHeight(ImagePreview preview, float width, float maxImageHeight)
    {
        var avail = MathF.Max(0f, width - ImagePreviewSurface.MatInset * 2f);
        var h = preview.Frames.Count > 1
            ? IconSheetLayout.Measure(preview.Frames, avail, maxImageHeight)
            : MathF.Min(MathF.Min(avail, preview.Width) * preview.Height / preview.Width, maxImageHeight);
        return h + ImagePreviewSurface.MatInset * 2f + CaptionHeight;
    }

    private static string? Caption(DiffViewModel vm, ILocalizationService loc)
    {
        if (vm.RenderState.Value is not DiffRenderState.Image image) return null;
        var s = loc.Strings.Value;
        var p = image.Preview;
        var size = FormatBytes(s, p.SourceBytes);
        // A container's frames each carry their own size label, so the caption counts them instead
        // of naming one entry's dimensions as though they were the file's.
        var caption = p.Frames.Count > 1
            ? $"{s.DiffImageSizeCount(p.Frames.Count)} · {size}"
            : s.DiffImageCaption(p.Width, p.Height, size);
        return image.IsOldSide ? $"{s.DiffImagePreviousVersion} · {caption}" : caption;
    }

    private static string FormatBytes(Strings s, int bytes)
    {
        if (bytes < 1024) return s.DiffImageSizeBytes(bytes);
        if (bytes < 1024 * 1024) return s.DiffImageSizeKilobytes((bytes / 1024f).ToString("0.#", s.Culture));
        return s.DiffImageSizeMegabytes((bytes / (1024f * 1024f)).ToString("0.#", s.Culture));
    }
}

/// <summary>
/// Draws a decoded image on a sunken mat: a single picture aspect-fitted and centered, or — for a
/// container carrying several drawings — every frame as a labelled contact sheet. The pixels are
/// uploaded to the canvas as dynamic textures under ids unique to this surface, so two panes
/// showing the same blob never share (and so never free) each other's textures; they are replaced
/// when the content changes and released when the surface unmounts.
/// </summary>
internal sealed class ImagePreviewSurface : View
{
    /// <summary>Padding between the pane edge and the mat the image is fitted into.</summary>
    internal const float MatInset = 12f;

    private static readonly TextStyle LabelStyle = new()
    {
        FontSize = FontSize.Caption,
        HorizontalAlignment = TextAlignment.Center,
        VerticalAlignment = TextAlignment.Center,
    };

    private static int _nextInstance;

    private readonly string _imageId = $"diff-image:{Interlocked.Increment(ref _nextInstance)}";
    private readonly ILocalizationService _loc;
    private readonly List<IconSheetCell> _cells = [];

    private ImagePreview? _preview;
    private bool _labelDepths;
    private ulong _uploadedHash;
    private int _uploadedFrames;
    // Captured on draw so the unmount path can release the textures — a detaching view has no
    // canvas of its own to ask.
    private ICanvas? _canvas;

    private uint _matColor;
    private uint _matBorderColor;
    private uint _labelColor;

    public ImagePreviewSurface(Context ctx)
    {
        _loc = ctx.Localization();
        this.BindThemed(ctx.Theme(), s =>
        {
            _matColor = s.Palette.SurfaceSunken;
            _matBorderColor = s.Palette.BorderSubtle;
            _labelColor = s.DiffContent.PlaceholderText;
            SetDirty();
        });
        this.Bind(_loc.Strings, _ => SetDirty());
        Behaviors.Add(new ReleaseTextureBehavior());
    }

    public void SetPreview(ImagePreview? preview)
    {
        if (ReferenceEquals(_preview, preview)) return;
        _preview = preview;
        _labelDepths = preview != null && HasRepeatedSize(preview.Frames);
        SetDirty();
    }

    protected override void OnDrawSelf(ICanvas c)
    {
        _canvas = c;
        if (_preview is not { } preview) return;

        var uploaded = Upload(c, preview);
        var z = GetDrawZIndex();

        if (preview.Frames.Count == 1)
        {
            DrawFrame(c, FitRect(preview.Primary), 0, uploaded, z);
            return;
        }

        IconSheetLayout.Arrange(preview.Frames, ContentRect(), _cells);
        LabelStyle.TextColor = _labelColor;
        foreach (var cell in _cells)
        {
            DrawFrame(c, cell.Image, cell.FrameIndex, uploaded, z);
            c.DrawText(new DrawTextInputs
            {
                Position = cell.Label,
                Text = FrameLabel(preview.Frames[cell.FrameIndex]),
                Style = LabelStyle,
                ZIndex = z + 1,
            });
        }
    }

    private void DrawFrame(ICanvas c, RectF rect, int frameIndex, bool uploaded, int z)
    {
        if (rect.Width < 1f || rect.Height < 1f) return;

        c.DrawRect(new DrawRectInputs
        {
            Position = rect,
            Style = new RectStyle
            {
                BackgroundColor = _matColor,
                BorderSize = BorderSizeStyle.All(1),
                BorderColor = BorderColorStyle.All(_matBorderColor),
            },
            ZIndex = z,
        });

        if (!uploaded) return;
        c.DrawImage(new DrawImageInputs
        {
            Position = rect,
            ImageId = FrameId(frameIndex),
            ZIndex = z + 1,
            TintColor = 0xFFFFFFFF,
            Rotation = 0f,
        });
    }

    // Two entries of the same size are different drawings, told apart only by the depth they were
    // stored at. A container carrying such a pair labels every frame with its depth, so the sheet
    // reads as one ladder rather than a few labels that grew an extra field.
    private string FrameLabel(ImageFrame frame)
    {
        var s = _loc.Strings.Value;
        return _labelDepths && frame.BitDepth is { } depth
            ? s.DiffImageFrameSizeDepth(frame.Width, frame.Height, depth)
            : s.DiffImageFrameSize(frame.Width, frame.Height);
    }

    private static bool HasRepeatedSize(IReadOnlyList<ImageFrame> frames)
    {
        for (var i = 1; i < frames.Count; i++)
            if (frames[i].Width == frames[i - 1].Width && frames[i].Height == frames[i - 1].Height)
                return true;
        return false;
    }

    private RectF ContentRect()
    {
        var pos = Position;
        return new RectF(
            pos.Left + MatInset,
            pos.Bottom + MatInset,
            MathF.Max(0f, pos.Width - MatInset * 2f),
            MathF.Max(0f, pos.Height - MatInset * 2f));
    }

    // The image's on-screen rect: aspect-fitted inside the inset pane, and never magnified past
    // its own pixel size — blowing a 16px icon up to fill the pane is blur, not information.
    private RectF FitRect(ImageFrame frame)
    {
        var area = ContentRect();
        if (area.Width <= 0f || area.Height <= 0f) return default;

        var w = MathF.Min(area.Width, frame.Width);
        var h = MathF.Min(area.Height, frame.Height);
        var aspect = (float)frame.Width / frame.Height;
        if (w / h > aspect) w = h * aspect;
        else h = w / aspect;

        var pos = Position;
        return new RectF(
            MathF.Round(pos.Left + (pos.Width - w) * 0.5f),
            MathF.Round(pos.Bottom + (pos.Height - h) * 0.5f),
            MathF.Round(w),
            MathF.Round(h));
    }

    private string FrameId(int index) => $"{_imageId}#{index}";

    private bool Upload(ICanvas c, ImagePreview preview)
    {
        if (_uploadedFrames == preview.Frames.Count && _uploadedHash == preview.ContentHash) return true;

        Release();
        for (var i = 0; i < preview.Frames.Count; i++)
        {
            var frame = preview.Frames[i];
            if (!c.CreateOrUpdateRgbaImage(FrameId(i), frame.Width, frame.Height, frame.Rgba))
            {
                Release();
                return false;
            }
            _uploadedFrames = i + 1;
        }
        _uploadedHash = preview.ContentHash;
        return true;
    }

    private void Release()
    {
        for (var i = 0; i < _uploadedFrames; i++) _canvas?.RemoveImage(FrameId(i));
        _uploadedFrames = 0;
    }

    private sealed class ReleaseTextureBehavior : IViewBehavior
    {
        public void Attach(View view) { }
        public void Detach(View view) => ((ImagePreviewSurface)view).Release();
    }
}
