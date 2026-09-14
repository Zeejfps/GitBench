using GitBench.Widgets;
using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Desktop;

namespace GitBench.Controls;

public sealed class PopupTooltipService : ITooltipService
{
    private const int Gap = 8;

    private readonly IPopupWindowFactory _factory;
    private object? _currentOwner;
    private IPopupWindow? _currentPopup;

    public PopupTooltipService(IPopupWindowFactory factory)
    {
        _factory = factory;
    }

    public void Show(object owner, string text, ScreenRect anchorScreen)
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

        _currentOwner = owner;
        _currentPopup = _factory.Acquire(new PopupRequest
        {
            BuildRoot = ctx => Direction.Wrap(new TooltipView { Text = text }).BuildView(ctx),
            Place = size =>
            {
                var centerX = anchorScreen.X + anchorScreen.Width / 2;
                var preferred = new ScreenRect(
                    X: centerX - size.Width / 2,
                    Y: anchorScreen.Y + anchorScreen.Height + Gap,
                    Width: size.Width, Height: size.Height);
                var flipped = new ScreenRect(
                    X: centerX - size.Width / 2,
                    Y: anchorScreen.Y - Gap - size.Height,
                    Width: size.Width, Height: size.Height);
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
