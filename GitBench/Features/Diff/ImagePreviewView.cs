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

    private static string? Caption(DiffViewModel vm, ILocalizationService loc)
    {
        if (vm.RenderState.Value is not DiffRenderState.Image image) return null;
        var s = loc.Strings.Value;
        var p = image.Preview;
        var caption = s.DiffImageCaption(p.Width, p.Height, FormatBytes(s, p.SourceBytes));
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
/// Draws one decoded image, aspect-fitted and centered on a sunken mat. The pixels are uploaded
/// to the canvas as a dynamic texture under an id unique to this surface, so two panes showing
/// the same blob never share (and so never free) each other's texture; the texture is replaced
/// when the content changes and released when the surface unmounts.
/// </summary>
internal sealed class ImagePreviewSurface : View
{
    private const float MatInset = 12f;

    private static int _nextInstance;

    private readonly string _imageId = $"diff-image:{Interlocked.Increment(ref _nextInstance)}";

    private ImagePreview? _preview;
    private ulong _uploadedHash;
    private bool _uploaded;
    // Captured on draw so the unmount path can release the texture — a detaching view has no
    // canvas of its own to ask.
    private ICanvas? _canvas;

    private uint _matColor;
    private uint _matBorderColor;

    public ImagePreviewSurface(Context ctx)
    {
        this.BindThemed(ctx.Theme(), s =>
        {
            _matColor = s.Palette.SurfaceSunken;
            _matBorderColor = s.Palette.BorderSubtle;
            SetDirty();
        });
        Behaviors.Add(new ReleaseTextureBehavior());
    }

    public void SetPreview(ImagePreview? preview)
    {
        if (ReferenceEquals(_preview, preview)) return;
        _preview = preview;
        SetDirty();
    }

    protected override void OnDrawSelf(ICanvas c)
    {
        _canvas = c;
        if (_preview is not { } preview) return;

        var rect = FitRect(preview);
        if (rect.Width < 1f || rect.Height < 1f) return;

        var z = GetDrawZIndex();
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

        if (!Upload(c, preview)) return;
        c.DrawImage(new DrawImageInputs
        {
            Position = rect,
            ImageId = _imageId,
            ZIndex = z + 1,
            TintColor = 0xFFFFFFFF,
            Rotation = 0f,
        });
    }

    // The image's on-screen rect: aspect-fitted inside the inset pane, and never magnified past
    // its own pixel size — blowing a 16px icon up to fill the pane is blur, not information.
    private RectF FitRect(ImagePreview preview)
    {
        var pos = Position;
        var availW = pos.Width - MatInset * 2f;
        var availH = pos.Height - MatInset * 2f;
        if (availW <= 0f || availH <= 0f) return default;

        var w = MathF.Min(availW, preview.Width);
        var h = MathF.Min(availH, preview.Height);
        var aspect = (float)preview.Width / preview.Height;
        if (w / h > aspect) w = h * aspect;
        else h = w / aspect;

        return new RectF(
            MathF.Round(pos.Left + (pos.Width - w) * 0.5f),
            MathF.Round(pos.Bottom + (pos.Height - h) * 0.5f),
            MathF.Round(w),
            MathF.Round(h));
    }

    private bool Upload(ICanvas c, ImagePreview preview)
    {
        if (_uploaded && _uploadedHash == preview.ContentHash) return true;
        if (!c.CreateOrUpdateRgbaImage(_imageId, preview.Width, preview.Height, preview.Rgba))
            return false;
        _uploaded = true;
        _uploadedHash = preview.ContentHash;
        return true;
    }

    private void Release()
    {
        if (!_uploaded) return;
        _canvas?.RemoveImage(_imageId);
        _uploaded = false;
    }

    private sealed class ReleaseTextureBehavior : IViewBehavior
    {
        public void Attach(View view) { }
        public void Detach(View view) => ((ImagePreviewSurface)view).Release();
    }
}
