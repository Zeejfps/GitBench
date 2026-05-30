using System.Runtime.Versioning;
using GLFW;
using ZGF.Core.MacOs;
using ZGF.Geometry;
using ZGF.Gui;
using static ZGF.Core.MacOs.Objc;

namespace GitGui;

[SupportedOSPlatform("macos")]
internal sealed class MacOsPopupDecorator : IPopupNativeDecorator
{
    // CGFloat for NSWindowLevel etc; on AppKit these are nullable ints exposed as NSInteger.
    private const long NSPopUpMenuWindowLevel = 101;

    // NSWindowCollectionBehavior flags (NSUInteger).
    private const ulong NSWindowCollectionBehaviorTransient = 1UL << 3;
    private const ulong NSWindowCollectionBehaviorIgnoresCycle = 1UL << 6;
    private const ulong NSWindowCollectionBehaviorCanJoinAllSpaces = 1UL << 0;

    private IntPtr? _localMonitor;
    private IntPtr? _globalMonitor;
    private IntPtr _capturedNsWindow;
    private Action<PointI>? _activeCallback;

    public void DecoratePopup(IntPtr glfwHandle, bool mousePassThrough)
    {
        var nsWindow = NsWindowFromGlfw(glfwHandle);
        if (nsWindow == IntPtr.Zero) return;

        msg_Void_Bool(nsWindow, Sel("setHasShadow:"), true);
        SetLevel(nsWindow, NSPopUpMenuWindowLevel);
        msg_Void_Bool(nsWindow, Sel("setHidesOnDeactivate:"), false);
        msg_Void_ULong(nsWindow, Sel("setCollectionBehavior:"),
            NSWindowCollectionBehaviorTransient
            | NSWindowCollectionBehaviorIgnoresCycle
            | NSWindowCollectionBehaviorCanJoinAllSpaces);
        if (mousePassThrough)
            msg_Void_Bool(nsWindow, Sel("setIgnoresMouseEvents:"), true);
    }

    public void BeginCapture(IntPtr glfwHandle, Action<PointI> onOutsideClick)
    {
        // NSEvent block-trampoline monitors are not yet implemented; the macOS path
        // currently relies on main-window-click dismissal (source #1 in the spec).
        // External clicks (other apps / desktop) will NOT dismiss popups on macOS
        // until the block trampoline is added. Tracked as deferred work.
        _capturedNsWindow = NsWindowFromGlfw(glfwHandle);
        _activeCallback = onOutsideClick;
    }

    public void EndCapture(IntPtr glfwHandle)
    {
        _capturedNsWindow = IntPtr.Zero;
        _activeCallback = null;
    }

    public void TransferCapture(IntPtr fromGlfw, IntPtr toGlfw, Action<PointI> onOutsideClick)
    {
        _capturedNsWindow = NsWindowFromGlfw(toGlfw);
        _activeCallback = onOutsideClick;
    }

    private static IntPtr NsWindowFromGlfw(IntPtr glfwHandle)
    {
        if (glfwHandle == IntPtr.Zero) return IntPtr.Zero;
        return Native.GetCocoaWindow((Window)glfwHandle);
    }

    [System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SetLevel(IntPtr receiver, IntPtr selector, long level);

    private static void SetLevel(IntPtr nsWindow, long level)
    {
        SetLevel(nsWindow, Sel("setLevel:"), level);
    }
}
