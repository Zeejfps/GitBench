using System.Runtime.InteropServices;
using ZGF.Fonts;
using ZGF.Gui;
using ZGF.Gui.Metal;
using ZGF.Gui.OpenGL;
using ZGF.Rendering.Metal;
using Xunit;
using static ZGF.Rendering.Metal.Objc;

namespace GitBench.Tests;

/// <summary>Runs pixel assertions against the platform's production shaders.</summary>
internal sealed class GpuTestSurface(
    RenderedCanvasBase canvas, int width, int height, Func<View, byte[]> render, Action dispose) : IDisposable
{
    public RenderedCanvasBase Canvas => canvas;
    public int Width => width;
    public int Height => height;
    public byte[] Render(View root) => render(root);
    public void Dispose() => dispose();

    public static GpuTestSurface Create(int width, int height, float scale, FreeTypeFontBackend fonts, FontHandle font)
        => OperatingSystem.IsMacOS()
            ? CreateMetal(width, height, scale, fonts, font)
            : CreateOpenGl(width, height, scale, fonts, font);

    private static GpuTestSurface CreateOpenGl(int width, int height, float scale, FreeTypeFontBackend fonts, FontHandle font)
    {
        GLFW.Glfw.Init();
        GLFW.Glfw.DefaultWindowHints();
        GLFW.Glfw.WindowHint(GLFW.Hint.ContextVersionMajor, 4);
        GLFW.Glfw.WindowHint(GLFW.Hint.ContextVersionMinor, 1);
        GLFW.Glfw.WindowHint(GLFW.Hint.Visible, false);
        var pixelWidth = (int)MathF.Round(width * scale);
        var pixelHeight = (int)MathF.Round(height * scale);
        var window = GLFW.Glfw.CreateWindow(pixelWidth, pixelHeight, "Settings rendering test", GLFW.Monitor.None, GLFW.Window.None);
        GlImageManager? images = null;
        GlSharedResources? shared = null;
        OpenGlRenderedCanvas? canvas = null;
        void Cleanup()
        {
            canvas?.Dispose();
            shared?.Dispose();
            images?.Dispose();
            GLFW.Glfw.DestroyWindow(window);
            GLFW.Glfw.Terminate();
        }
        try
        {
            GLFW.Glfw.MakeContextCurrent(window);
            GL46.Import(GLFW.Glfw.GetProcAddress);
            images = new GlImageManager();
            shared = new GlSharedResources(fonts, images);
            canvas = new OpenGlRenderedCanvas(width, height, fonts, font, shared, scale);
            return new GpuTestSurface(canvas, pixelWidth, pixelHeight, root =>
            {
                GL46.glClearColor(0, 0, 0, 1);
                GL46.glClear(GL46.GL_COLOR_BUFFER_BIT);
                canvas.BeginFrame();
                root.DrawSelf(canvas);
                canvas.EndFrame();
                return canvas.ReadFramebufferRgba(out _, out _);
            }, Cleanup);
        }
        catch { Cleanup(); throw; }
    }

    private static GpuTestSurface CreateMetal(int width, int height, float scale, FreeTypeFontBackend fonts, FontHandle font)
    {
        // Offscreen texture: no Cocoa window or main-thread event loop is needed.
        var pool = New(Class("NSAutoreleasePool"));
        var device = MetalApi.MTLCreateSystemDefaultDevice();
        var queue = IntPtr.Zero;
        var texture = IntPtr.Zero;
        MetalImageManager? images = null;
        MetalSharedResources? shared = null;
        MetalRenderedCanvas? canvas = null;
        void Cleanup()
        {
            canvas?.Dispose();
            shared?.Dispose();
            images?.Dispose();
            Release(texture);
            Release(queue);
            Release(device);
            Release(pool);
        }
        try
        {
            Assert.NotEqual(IntPtr.Zero, device);
            queue = msg_IntPtr(device, Sel("newCommandQueue"));
            Assert.NotEqual(IntPtr.Zero, queue);
            images = new MetalImageManager(device);
            shared = new MetalSharedResources(device, queue, fonts, images);
            canvas = new MetalRenderedCanvas(width, height, fonts, font, shared, scale);
            var pixelWidth = (int)MathF.Round(width * scale);
            var pixelHeight = (int)MathF.Round(height * scale);
            var desc = msg_IntPtr_NUInt_NUInt_NUInt_Bool(Class("MTLTextureDescriptor"),
                Sel("texture2DDescriptorWithPixelFormat:width:height:mipmapped:"),
                (nuint)MTLPixelFormat.BGRA8Unorm, (nuint)pixelWidth, (nuint)pixelHeight, false);
            msg_Void_UInt(desc, Sel("setStorageMode:"), (uint)MTLStorageMode.Shared);
            msg_Void_UInt(desc, Sel("setUsage:"), (uint)MTLTextureUsage.RenderTarget);
            texture = msg_IntPtr(device, Sel("newTextureWithDescriptor:"), desc);
            Assert.NotEqual(IntPtr.Zero, texture);
            return new GpuTestSurface(canvas, pixelWidth, pixelHeight, root =>
            {
                var pass = msg_IntPtr(Class("MTLRenderPassDescriptor"), Sel("renderPassDescriptor"));
                var attachments = msg_IntPtr(pass, Sel("colorAttachments"));
                var color = msg_IntPtr_NUInt_NUInt(attachments, Sel("objectAtIndexedSubscript:"), 0, 0);
                msg_Void_IntPtr(color, Sel("setTexture:"), texture);
                msg_Void_UInt(color, Sel("setLoadAction:"), 2); // clear
                msg_Void_UInt(color, Sel("setStoreAction:"), 1); // store
                var command = msg_IntPtr(queue, Sel("commandBuffer"));
                var encoder = msg_IntPtr(command, Sel("renderCommandEncoderWithDescriptor:"), pass);
                Assert.NotEqual(IntPtr.Zero, encoder);
                canvas.BeginFrame();
                root.DrawSelf(canvas);
                canvas.EndFrame(encoder, command);
                msg_Void(encoder, Sel("endEncoding"));
                msg_Void(command, Sel("commit"));
                msg_Void(command, Sel("waitUntilCompleted"));
                Assert.Equal((nuint)4, msg_NUInt(command, Sel("status"))); // completed
                var pixels = new byte[pixelWidth * pixelHeight * 4];
                var region = new MTLRegion
                {
                    Size = new MTLSize { Width = (nuint)pixelWidth, Height = (nuint)pixelHeight, Depth = 1 },
                };
                // Pin only for the synchronous native read; Metal returns top-down BGRA.
                var pinned = GCHandle.Alloc(pixels, GCHandleType.Pinned);
                try
                {
                    msg_GetBytes(texture, Sel("getBytes:bytesPerRow:fromRegion:mipmapLevel:"),
                        pinned.AddrOfPinnedObject(), (nuint)(pixelWidth * 4), region, 0);
                }
                finally { pinned.Free(); }
                for (var i = 0; i < pixels.Length; i += 4)
                    (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
                return pixels;
            }, Cleanup);
        }
        catch { Cleanup(); throw; }
    }
}
