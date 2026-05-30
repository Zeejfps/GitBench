using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using GLFW;
using ZGF.Geometry;
using ZGF.Gui;

namespace GitGui;

[SupportedOSPlatform("windows")]
internal sealed class WindowsPopupDecorator : IPopupNativeDecorator
{
    private const int GWL_EXSTYLE = -20;
    private const int GWLP_WNDPROC = -4;

    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const long WS_EX_TOPMOST = 0x00000008;
    private const long WS_EX_TRANSPARENT = 0x00000020;
    private const long WS_EX_NOACTIVATE = 0x08000000;

    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const uint SWP_NOACTIVATE = 0x0010;

    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_RBUTTONDOWN = 0x0204;
    private const uint WM_MBUTTONDOWN = 0x0207;
    private const uint WM_NCLBUTTONDOWN = 0x00A1;
    private const uint WM_NCRBUTTONDOWN = 0x00A4;
    private const uint WM_CANCELMODE = 0x001F;

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    private sealed class Subclass
    {
        public IntPtr OriginalProc;
        public Action<PointI> Callback = _ => { };
        public WndProc NewProc = null!;
    }

    private readonly Dictionary<IntPtr, Subclass> _subclasses = new();
    private IntPtr _capturedHwnd = IntPtr.Zero;

    public void DecoratePopup(IntPtr glfwHandle, bool mousePassThrough)
    {
        var hwnd = GetHwnd(glfwHandle);
        if (hwnd == IntPtr.Zero) return;

        var ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        ex |= WS_EX_TOOLWINDOW;
        ex |= WS_EX_NOACTIVATE;
        ex |= WS_EX_TOPMOST;
        if (mousePassThrough) ex |= WS_EX_TRANSPARENT;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED | SWP_NOACTIVATE);
    }

    public void BeginCapture(IntPtr glfwHandle, Action<PointI> onOutsideClick)
    {
        var hwnd = GetHwnd(glfwHandle);
        if (hwnd == IntPtr.Zero) return;

        InstallSubclass(hwnd, onOutsideClick);
        SetCapture(hwnd);
        _capturedHwnd = hwnd;
    }

    public void EndCapture(IntPtr glfwHandle)
    {
        var hwnd = GetHwnd(glfwHandle);
        if (hwnd == IntPtr.Zero) return;

        if (_capturedHwnd == hwnd)
        {
            ReleaseCapture();
            _capturedHwnd = IntPtr.Zero;
        }
        RemoveSubclass(hwnd);
    }

    public void TransferCapture(IntPtr fromGlfw, IntPtr toGlfw, Action<PointI> onOutsideClick)
    {
        var fromHwnd = GetHwnd(fromGlfw);
        var toHwnd = GetHwnd(toGlfw);
        if (toHwnd == IntPtr.Zero) return;

        RemoveSubclass(fromHwnd);
        if (_capturedHwnd != IntPtr.Zero) ReleaseCapture();

        InstallSubclass(toHwnd, onOutsideClick);
        SetCapture(toHwnd);
        _capturedHwnd = toHwnd;
    }

    private void InstallSubclass(IntPtr hwnd, Action<PointI> callback)
    {
        if (_subclasses.TryGetValue(hwnd, out var existing))
        {
            existing.Callback = callback;
            return;
        }
        var sub = new Subclass { Callback = callback };
        sub.NewProc = (h, msg, w, l) => SubclassProc(sub, h, msg, w, l);
        sub.OriginalProc = SetWindowLongPtr(hwnd, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(sub.NewProc));
        _subclasses[hwnd] = sub;
    }

    private void RemoveSubclass(IntPtr hwnd)
    {
        if (!_subclasses.TryGetValue(hwnd, out var sub)) return;
        SetWindowLongPtr(hwnd, GWLP_WNDPROC, sub.OriginalProc);
        _subclasses.Remove(hwnd);
    }

    private static IntPtr SubclassProc(Subclass sub, IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_LBUTTONDOWN:
            case WM_RBUTTONDOWN:
            case WM_MBUTTONDOWN:
            case WM_NCLBUTTONDOWN:
            case WM_NCRBUTTONDOWN:
            {
                var screen = ScreenPointFromLParam(lParam, hwnd, msg);
                var gotRect = GetWindowRectStruct(hwnd, out var r);
                var insideRect = gotRect && ContainsPoint(r, screen);
                if (!insideRect)
                {
                    sub.Callback(screen);
                    // SetCapture routed this click to the popup hwnd. Re-route
                    // to the window actually under the cursor so the underlying
                    // control (e.g. another repo row) still receives the press.
                    // Without this, a click outside the popup only dismisses
                    // the menu and is otherwise lost.
                    ReleaseCapture();
                    var pt = new POINT { X = screen.X, Y = screen.Y };
                    var target = WindowFromPoint(pt);
                    if (target != IntPtr.Zero && target != hwnd)
                    {
                        ScreenToClient(target, ref pt);
                        var lp = (IntPtr)(((pt.Y & 0xFFFF) << 16) | (pt.X & 0xFFFF));
                        PostMessage(target, msg, wParam, lp);
                    }
                    return IntPtr.Zero;
                }
                break;
            }
            case WM_CANCELMODE:
            {
                sub.Callback(new PointI(int.MinValue, int.MinValue));
                break;
            }
        }
        return CallWindowProc(sub.OriginalProc, hwnd, msg, wParam, lParam);
    }

    private static PointI ScreenPointFromLParam(IntPtr lParam, IntPtr hwnd, uint msg)
    {
        var x = (short)(lParam.ToInt64() & 0xFFFF);
        var y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
        // NC messages already pass screen coords; client messages pass client coords.
        if (msg is WM_NCLBUTTONDOWN or WM_NCRBUTTONDOWN)
            return new PointI(x, y);

        var pt = new POINT { X = x, Y = y };
        ClientToScreen(hwnd, ref pt);
        return new PointI(pt.X, pt.Y);
    }

    private static bool ContainsPoint(RECT r, PointI p) =>
        p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;

    private static bool GetWindowRectStruct(IntPtr hwnd, out RECT r)
    {
        var ok = GetWindowRect(hwnd, out r);
        return ok;
    }

    private static IntPtr GetHwnd(IntPtr glfwHandle)
    {
        if (glfwHandle == IntPtr.Zero) return IntPtr.Zero;
        var window = (Window)glfwHandle;
        return Native.GetWin32Window(window);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr hwndAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCapture(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hwnd, ref POINT pt);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT pt);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr hwnd, ref POINT pt);

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
}
