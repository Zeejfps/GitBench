using ZGF.Geometry;
using ZGF.Gui;

namespace GitGui;

internal sealed class NoopPopupDecorator : IPopupNativeDecorator
{
    public void DecoratePopup(IntPtr nativeWindowHandle, bool mousePassThrough) { }
    public void BeginCapture(IntPtr nativeWindowHandle, Action<PointI> onOutsideClick) { }
    public void EndCapture(IntPtr nativeWindowHandle) { }
    public void TransferCapture(IntPtr fromHandle, IntPtr toHandle, Action<PointI> onOutsideClick) { }
}
