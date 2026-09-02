using GitBench.Features.Diff;
using GitBench.Widgets;
using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Repos;

/// <summary>
/// A small, aspect-fitted sidebar image. It uses the same bounded PNG/JPEG/ICO decoder as image
/// previews, uploads only the best frame, and releases the canvas texture when its row unmounts.
/// The widget renders nothing on failure so the folder glyph or initials behind it remain visible.
/// </summary>
internal sealed record RepoIconImage : Widget
{
    public required string Path { get; init; }
    public required float Size { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var frame = Load(Path);
        if (frame is null) return Empty.Widget;
        return new Raw
        {
            View = new RepoIconImageSurface(frame) { Width = Size, Height = Size },
        };
    }

    internal static bool CanLoad(string path) => Load(path) is not null;

    private static ImageFrame? Load(string path)
    {
        try
        {
            if (!ImagePreviewDecoder.IsPreviewablePath(path)) return null;
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= 0 || info.Length > ImagePreviewDecoder.MaxSourceBytes)
                return null;
            return ImagePreviewDecoder.TryDecode(File.ReadAllBytes(path))?.Primary;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private sealed class RepoIconImageSurface : View
    {
        private static int _nextId;

        private readonly ImageFrame _frame;
        private readonly string _imageId = $"repo-icon:{Interlocked.Increment(ref _nextId)}";
        private ICanvas? _canvas;
        private bool _uploaded;

        public RepoIconImageSurface(ImageFrame frame)
        {
            _frame = frame;
            Behaviors.Add(new ReleaseTextureBehavior());
        }

        protected override void OnDrawSelf(ICanvas canvas)
        {
            _canvas = canvas;
            if (!_uploaded)
                _uploaded = canvas.CreateOrUpdateRgbaImage(_imageId, _frame.Width, _frame.Height, _frame.Rgba);
            if (!_uploaded) return;

            var bounds = Position;
            var scale = MathF.Min(bounds.Width / _frame.Width, bounds.Height / _frame.Height);
            var width = MathF.Max(1f, MathF.Round(_frame.Width * scale));
            var height = MathF.Max(1f, MathF.Round(_frame.Height * scale));
            var rect = new RectF(
                MathF.Round(bounds.Left + (bounds.Width - width) * 0.5f),
                MathF.Round(bounds.Bottom + (bounds.Height - height) * 0.5f),
                width,
                height);

            canvas.DrawImage(new DrawImageInputs
            {
                Position = rect,
                ImageId = _imageId,
                ZIndex = GetDrawZIndex(),
                TintColor = 0xFFFFFFFF,
                Rotation = 0f,
            });
        }

        private void Release()
        {
            if (_uploaded) _canvas?.RemoveImage(_imageId);
            _uploaded = false;
        }

        private sealed class ReleaseTextureBehavior : IViewBehavior
        {
            public void Attach(View view) { }
            public void Detach(View view) => ((RepoIconImageSurface)view).Release();
        }
    }
}
