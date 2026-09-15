using ZGF.Gui;

namespace GitBench.Features.Diff;

/// <summary>
/// One RGBA texture a view keeps on the canvas it draws to: sent up on the first draw that has a
/// frame, sent again after <see cref="Invalidate"/>, and taken back down on <see cref="Release"/>.
/// The canvas is remembered from the draw because a detaching view has none of its own to ask.
/// </summary>
internal sealed class UploadedImage
{
    private static int _nextId;

    private ICanvas? _canvas;
    private bool _uploaded;

    public string Id { get; } = $"image:{Interlocked.Increment(ref _nextId)}";

    /// <summary>Whether the frame is on the canvas after this call; false on a canvas that draws
    /// no images.</summary>
    public bool Ensure(ICanvas canvas, ImageFrame frame)
    {
        _canvas = canvas;
        if (!_uploaded)
            _uploaded = canvas.CreateOrUpdateRgbaImage(Id, frame.Width, frame.Height, frame.Rgba);
        return _uploaded;
    }

    /// <summary>The next draw re-sends the frame under the same id, replacing what is there.</summary>
    public void Invalidate() => _uploaded = false;

    public void Release()
    {
        if (_uploaded) _canvas?.RemoveImage(Id);
        _uploaded = false;
    }
}
