using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Desktop;

namespace GitBench.Controls;

public sealed class PopupTooltipService : ITooltipService
{
    private const int Gap = 8;

    private readonly IPopupWindowFactory _factory;
    private readonly IWindowCoordinates _coordinates;
    private object? _currentOwner;
    private IPopupWindow? _currentPopup;

    public PopupTooltipService(IPopupWindowFactory factory, IWindowCoordinates coordinates)
    {
        _factory = factory;
        _coordinates = coordinates;
    }

    public void Show(object owner, string text, RectF anchorRectCanvas)
    {
        // Release whatever's currently up regardless of owner. Hide(owner) is a
        // no-op when a different owner held the previous tooltip, which would
        // leak its popup native handle on every owner transition.
        if (_currentPopup != null)
        {
            _factory.Release(_currentPopup);
            _currentPopup = null;
            _currentOwner = null;
        }

        var anchorScreen = _coordinates.ToScreenPoints(anchorRectCanvas);

        _currentOwner = owner;
        _currentPopup = _factory.Acquire(new PopupRequest
        {
            BuildRoot = ctx =>
            {
                using (CompatUi.Push(ctx))
                    return ViewContexts.RegisterRoot(new TooltipView(text), ctx);
            },
            Place = (width, height) =>
            {
                var centerX = anchorScreen.X + anchorScreen.Width / 2;
                var preferred = new RectI(
                    X: centerX - width / 2,
                    Y: anchorScreen.Y + anchorScreen.Height + Gap,
                    Width: width, Height: height);
                var flipped = new RectI(
                    X: centerX - width / 2,
                    Y: anchorScreen.Y - Gap - height,
                    Width: width, Height: height);
                return (preferred, flipped);
            },
            MousePassThrough = true,
        });
    }

    public void Hide(object owner)
    {
        if (!ReferenceEquals(_currentOwner, owner)) return;
        if (_currentPopup != null) _factory.Release(_currentPopup);
        _currentPopup = null;
        _currentOwner = null;
    }
}
