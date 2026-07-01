using ZGF.Geometry;
using ZGF.Gui.Desktop;

namespace GitBench.Platform;

internal sealed class NoopPopupDecorator : IPopupNativeDecorator
{
    public void DecoratePopup(IntPtr nativeWindowHandle) { }
    public void SetMousePassThrough(IntPtr nativeWindowHandle, bool passThrough) { }
    public void BeginCapture(IntPtr nativeWindowHandle, Action<PointI> onOutsideClick) { }
    public void EndCapture(IntPtr nativeWindowHandle) { }
    public void TransferCapture(IntPtr fromHandle, IntPtr toHandle, Action<PointI> onOutsideClick) { }
    public void WatchWindowNonClientPress(IntPtr nativeWindowHandle, Action onNonClientPress) { }
    public void UnwatchWindow(IntPtr nativeWindowHandle) { }
}
