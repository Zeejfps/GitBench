using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static ZGF.Rendering.Metal.Objc;

namespace GitBench.Platform;

/// <summary>
/// Sets the Dock icon for non-bundled (dev) runs via NSApplication.applicationIconImage — GLFW
/// can't, and without a .app bundle the Dock shows the generic executable icon. Inside a bundle
/// this is a no-op: the Dock styles the bundle's icns itself, and macOS 26+ applies the squircle
/// mask only to bundle icons, so a runtime image must round its own corners (done here) and must
/// not replace an already system-styled icon.
/// </summary>
[SupportedOSPlatform("macos")]
internal static class MacOsDockIcon
{
    // macOS 26 grid, measured off a system-themed Dock icon: content square ~888/1024 (86.7%).
    // The real mask is a superellipse; a rounded rect at ~26% radius is the closest eyeball match
    // at Dock sizes. The source asset keeps the flat art at 824/1024, so the draw maps that region
    // onto the 888 target.
    private const double Canvas = 1024.0;
    private const double Content = 888.0;
    private const double CornerRadius = Content * 0.26;
    private const double SourceInset = 100.0;
    private const double SourceContent = 824.0;

    private const ulong NSCompositingOperationSourceOver = 2;

    public static void Set(string imagePath)
    {
        var bundlePath = msg_IntPtr(msg_IntPtr(Class("NSBundle"), Sel("mainBundle")), Sel("bundlePath"));
        var path = bundlePath == IntPtr.Zero
            ? null
            : Marshal.PtrToStringUTF8(msg_IntPtr(bundlePath, Sel("UTF8String")));
        if (path?.EndsWith(".app", StringComparison.Ordinal) == true)
            return;

        var image = msg_IntPtr(msg_IntPtr(Class("NSImage"), Sel("alloc")),
            Sel("initWithContentsOfFile:"), NSString(imagePath));
        if (image == IntPtr.Zero) return;

        var app = msg_IntPtr(Class("NSApplication"), Sel("sharedApplication"));
        if (app == IntPtr.Zero) return;

        msg_Void_IntPtr(app, Sel("setApplicationIconImage:"), RoundCorners(image));
    }

    private static IntPtr RoundCorners(IntPtr image)
    {
        var rounded = msg_IntPtr_CGSizeArg(msg_IntPtr(Class("NSImage"), Sel("alloc")),
            Sel("initWithSize:"), new CGSizeD(Canvas, Canvas));
        if (rounded == IntPtr.Zero) return image;

        var inset = (Canvas - Content) / 2;
        msg_Void(rounded, Sel("lockFocus"));
        var clip = msg_IntPtr_CGRect_Double_Double(Class("NSBezierPath"),
            Sel("bezierPathWithRoundedRect:xRadius:yRadius:"),
            new CGRectD(inset, inset, Content, Content), CornerRadius, CornerRadius);
        if (clip != IntPtr.Zero)
            msg_Void(clip, Sel("addClip"));
        msg_Void_CGRect_CGRect_ULong_Double(image, Sel("drawInRect:fromRect:operation:fraction:"),
            new CGRectD(inset, inset, Content, Content),
            new CGRectD(SourceInset, SourceInset, SourceContent, SourceContent),
            NSCompositingOperationSourceOver, 1.0);
        msg_Void(rounded, Sel("unlockFocus"));
        return rounded;
    }

    private static IntPtr NSString(string value)
    {
        var utf8 = Marshal.StringToCoTaskMemUTF8(value);
        try
        {
            return msg_IntPtr(Class("NSString"), Sel("stringWithUTF8String:"), utf8);
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct CGSizeD(double width, double height)
    {
        public readonly double Width = width;
        public readonly double Height = height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct CGRectD(double x, double y, double width, double height)
    {
        public readonly double X = x;
        public readonly double Y = y;
        public readonly double Width = width;
        public readonly double Height = height;
    }

    private const string LibObjc = "/usr/lib/libobjc.A.dylib";

    [DllImport(LibObjc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr msg_IntPtr_CGSizeArg(IntPtr receiver, IntPtr selector, CGSizeD size);

    [DllImport(LibObjc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr msg_IntPtr_CGRect_Double_Double(
        IntPtr receiver, IntPtr selector, CGRectD rect, double xRadius, double yRadius);

    [DllImport(LibObjc, EntryPoint = "objc_msgSend")]
    private static extern void msg_Void_CGRect_CGRect_ULong_Double(
        IntPtr receiver, IntPtr selector, CGRectD dest, CGRectD src, ulong op, double fraction);
}
