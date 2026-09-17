using GitBench.Controls;
using GitBench.Features.Repos;
using GitBench.Localization;
using GitBench.Theming;
using PngSharp.Api;
using ZGF.AppUtils;
using ZGF.Fonts;
using ZGF.Gui;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Testing;
using ZGF.Gui.Widgets;
using ZGF.KeyboardModule;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests;

public sealed class ColorPickerTests
{
    [Theory]
    [InlineData("#123", 0xFF112233u)]
    [InlineData(" abcDEF ", 0xFFABCDEFu)]
    [InlineData("000000", 0xFF000000u)]
    [InlineData("#fff", 0xFFFFFFFFu)]
    public void ParsesOpaqueHex(string text, uint expected)
    {
        Assert.True(HsvColor.TryParseHex(text, out var color));
        Assert.Equal(expected, color);
        Assert.Equal(expected, HsvColor.FromArgb(color).ToArgb());
    }

    [Theory]
    [InlineData(0, 1, 1, 0xFFFF0000u)]
    [InlineData(120, 1, 1, 0xFF00FF00u)]
    [InlineData(240, 1, 1, 0xFF0000FFu)]
    [InlineData(360, 1, 1, 0xFFFF0000u)]
    [InlineData(100, 0, 1, 0xFFFFFFFFu)]
    [InlineData(100, 1, 0, 0xFF000000u)]
    public void HsvHasExpectedCorners(float h, float s, float v, uint expected) =>
        Assert.Equal(expected, new HsvColor(h, s, v).ToArgb());

    [Theory]
    [InlineData("")]
    [InlineData("#12")]
    [InlineData("#12ZZ34")]
    [InlineData("#11223344")]
    public void InvalidHexPreservesLastSelectionAndResetRestoresDefault(string text)
    {
        using var model = new ColorPickerModel(null, 0xFF123456);
        Assert.Null(model.SelectedColor.Value);
        model.Select(0xFF4488AA);
        model.Hex.Value = text;
        Assert.False(model.IsValid.Value);
        Assert.Equal(0xFF4488AAu, model.Color);
        model.Reset();
        Assert.Null(model.SelectedColor.Value);
        Assert.True(model.IsValid.Value);
        Assert.Equal("#123456", model.Hex.Value);
    }

    [Fact]
    public void HueSurvivesBlackAndGrayAndValidTypingDoesNotRewriteTheField()
    {
        using var model = new ColorPickerModel(0xFF0000FF, 0xFF000000);
        model.Hex.Value = "#000";
        Assert.Equal(240, model.Hsv.Value.Hue);
        model.SetHsv(model.Hsv.Value with { Value = 1 });
        Assert.Equal(0xFF0000FFu, model.Color);
        model.Hex.Value = "#aaa";
        Assert.Equal("#aaa", model.Hex.Value);
        Assert.Equal(240, model.Hsv.Value.Hue);
        model.SetHsv(model.Hsv.Value with { Saturation = 1, Value = 1 });
        Assert.Equal(0xFF0000FFu, model.Color);
    }

    [Fact]
    public void MouseDragClampsAndKeyboardCanReachHueAndHex()
    {
        using var model = new ColorPickerModel(0xFFFF0000, 0xFF969BA7);
        using var h = GuiTestHarness.Create(ctx => new ColorPicker { Model = model, Width = 280 }.BuildView(ctx),
            width: 320, height: 560, configure: Configure);
        h.Layout();
        var surface = Assert.IsType<ColorPickerSurface>(h.Get(ColorPicker.AreaId));
        h.MoveTo(surface.Track.Center.X, surface.Track.Center.Y);
        h.Press();
        h.MoveTo(surface.Position.Right + 30, surface.Position.Top + 30);
        h.Release();
        Assert.Equal(1, model.Hsv.Value.Saturation);
        Assert.Equal(1, model.Hsv.Value.Value);
        h.PressKey(KeyboardKey.LeftArrow);
        Assert.Equal(0.99f, model.Hsv.Value.Saturation, 3);
        h.PressKey(KeyboardKey.Tab);
        h.PressKey(KeyboardKey.RightArrow, InputModifiers.Shift);
        Assert.Equal(10, model.Hsv.Value.Hue);
        h.PressKey(KeyboardKey.Tab);
        h.PressKey(KeyboardKey.A, InputModifiers.Control);
        h.Type("#00ff00");
        Assert.Equal(0xFF00FF00u, model.Color);
        h.ClickOn("color-picker-swatch-0");
        Assert.Equal(ColorPicker.Presets[0], model.Color);
        h.PressKey(KeyboardKey.Tab);
        h.ClickOn("color-picker-swatch-6");
        Assert.Equal(ColorPicker.Presets[6], model.Color);
        h.ClickOn(ColorPicker.ResetId);
        Assert.Null(model.SelectedColor.Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FocusedPickerCanCancelWithPointerOutside(bool swatchFocused)
    {
        using var model = new ColorPickerModel(null, 0xFF969BA7);
        var canceled = false;
        using var h = GuiTestHarness.Create(ctx => new Center
        {
            Child = new ColorPicker { Model = model, Width = 280, OnCancel = () => canceled = true },
        }.BuildView(ctx), width: 640, height: 700, configure: Configure);
        if (swatchFocused) h.PressKey(KeyboardKey.Tab, InputModifiers.Shift);
        h.MoveTo(0, 0);
        h.PressKey(KeyboardKey.Escape);
        Assert.True(canceled);
    }

    [Theory]
    [InlineData(ThemeMode.Light)]
    [InlineData(ThemeMode.Dark)]
    public void DialogFitsAndCanRenderWithRealFonts(ThemeMode theme)
    {
        using var dir = new TempDir("color-picker-layout-");
        var statePath = Path.Combine(dir.Path, "state.json");
        using var registry = new RepoRegistry(RepoStateStore.Load(statePath), statePath);
        using var fonts = new FreeTypeFontBackend();
        var font = fonts.LoadFontFromMemory(EmbeddedAssets.LoadBytes(typeof(Context).Assembly, "Inter-Regular.ttf"), 16);
        var iconFont = fonts.LoadFontFromMemory(EmbeddedAssets.LoadBytes(typeof(LucideIcons).Assembly, "Lucide.ttf"), 16);
        var dialog = new RepoCustomizeIconDialog { Repo = new(Guid.NewGuid(), dir.Path, "GitBench"), OnClose = () => { } };
        using var h = GuiTestHarness.CreateRaster(ctx => new Center { Child = dialog }.BuildView(ctx), fonts, font,
            width: 640, height: 700, configure: ctx =>
            {
                Configure(ctx, theme);
                ((RenderedCanvasBase)ctx.Canvas).RegisterFont(LucideIcons.FontFamily, iconFont);
                ctx.AddService<IRepoRegistry>(registry);
            });
        h.Layout();
        foreach (var id in new[] { ColorPicker.AreaId, ColorPicker.HueId, ColorPicker.HexId, ColorPicker.ResetId })
        {
            var rect = h.Get(id).Position;
            Assert.True(rect.Width > 0 && rect.Height > 0);
            Assert.True(rect.Left >= 0 && rect.Right <= 640 && rect.Bottom >= 0 && rect.Top <= 700);
        }
    }

    [GpuTheory]
    [InlineData(ThemeMode.Dark, 1f)]
    [InlineData(ThemeMode.Light, 1f)]
    [InlineData(ThemeMode.Dark, 1.5f)]
    public void GradientsFillTheirTracksAndUpdateOnGpu(ThemeMode theme, float scale)
    {
        using var fonts = new FreeTypeFontBackend();
        var font = fonts.LoadFontFromMemory(EmbeddedAssets.LoadBytes(typeof(Context).Assembly, "Inter-Regular.ttf"), (int)(16 * scale));
        var icons = fonts.LoadFontFromMemory(EmbeddedAssets.LoadBytes(typeof(LucideIcons).Assembly, "Lucide.ttf"), (int)(16 * scale));
        using var gpu = GpuTestSurface.Create(640, 700, scale, fonts, font);
        gpu.Canvas.RegisterFont(LucideIcons.FontFamily, icons);
        var dialog = new RepoCustomizeIconDialog
        {
            Repo = new(Guid.Parse("01234567-89ab-cdef-0123-456789abcdef"), "repo", "GitBench") { CustomColor = 0xFF6195E8 },
            OnClose = () => { },
        };
        using var h = GuiTestHarness.Create(ctx => new Center { Child = dialog }.BuildView(ctx),
            width: 640, height: 700, configure: ctx => { Configure(ctx, theme); ctx.Canvas = gpu.Canvas; });
        h.Layout();
        var compactPixels = gpu.Render(h.Root);
        var firstSwatch = h.Get("color-picker-swatch-0").Position;
        AssertPixel(compactPixels, firstSwatch.Center.X, firstSwatch.Center.Y, ColorPicker.Presets[0]);
        if (Environment.GetEnvironmentVariable("GITBENCH_COLOR_PICKER_SCREENSHOTS") is { } compactOutput)
        {
            Directory.CreateDirectory(compactOutput);
            Png.EncodeToFile(Png.CreateRgba(gpu.Width, gpu.Height, compactPixels), Path.Combine(compactOutput, $"compact-{theme}-{scale}.png"));
            foreach (var id in new[] { RepoCustomizeIconDialog.BrowseId, ColorPicker.ResetId })
            {
                var bounds = h.Get(id).Position;
                h.MoveTo(bounds.Center.X, bounds.Center.Y);
                var hovered = gpu.Render(h.Root);
                Png.EncodeToFile(Png.CreateRgba(gpu.Width, gpu.Height, hovered), Path.Combine(compactOutput, $"hover-{id}-{theme}-{scale}.png"));
            }
            h.MoveTo(0, 0);
        }
        var area = Assert.IsType<ColorPickerSurface>(h.Get(ColorPicker.AreaId));
        var hue = Assert.IsType<ColorPickerSurface>(h.Get(ColorPicker.HueId));
        foreach (var degrees in new[] { 0f, 120f })
        {
            dialog.State.Color.SetHsv(new(degrees, 0.5f, 0.5f));
            var pixels = gpu.Render(h.Root);
            foreach (var (saturation, value) in new[] { (0.1f, 0.9f), (0.9f, 0.9f), (0.9f, 0.1f) })
                AssertPixel(pixels, area.Track.Left + area.Track.Width * saturation, area.Track.Bottom + area.Track.Height * value,
                    new HsvColor(degrees, saturation, value).ToArgb());
            foreach (var y in new[] { 0.25f, 0.75f })
                AssertPixel(pixels, hue.Track.Left + hue.Track.Width * 0.5f, hue.Track.Bottom + hue.Track.Height * y, 0xFF00FFFF);
        }
        dialog.State.Color.Select(0xFF6195E8);
        var screenshot = gpu.Render(h.Root);
        if (Environment.GetEnvironmentVariable("GITBENCH_COLOR_PICKER_SCREENSHOTS") is { } output)
        {
            Directory.CreateDirectory(output);
            Png.EncodeToFile(Png.CreateRgba(gpu.Width, gpu.Height, screenshot), Path.Combine(output, $"color-picker-{theme}-{scale}.png"));
        }

        using var imageDir = new TempDir("customize-icon-gpu-");
        var imagePath = Path.Combine(imageDir.Path, "icon.png");
        Png.EncodeToFile(Png.CreateRgba(1, 1, [51, 204, 102, 255]), imagePath);
        dialog.State.SelectImage(imagePath, "Invalid image");
        h.Layout();
        var imagePixels = gpu.Render(h.Root);
        var preview = h.Get(RepoCustomizeIconDialog.ImagePreviewId).Position;
        AssertPixel(imagePixels, preview.Center.X, preview.Center.Y, 0xFF33CC66);
        // A preview already loaded into this dialog must survive a rebuild without
        // reopening its source. Keeping the file exclusively locked catches repeated reads.
        using (var lockedSource = new FileStream(imagePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            dialog.State.PreviewVersion.Value++;
            h.Layout();
            var reusedPixels = gpu.Render(h.Root);
            preview = h.Get(RepoCustomizeIconDialog.ImagePreviewId).Position;
            AssertPixel(reusedPixels, preview.Center.X, preview.Center.Y, 0xFF33CC66);
        }
        if (Environment.GetEnvironmentVariable("GITBENCH_COLOR_PICKER_SCREENSHOTS") is { } imageOutput)
        {
            dialog.State.SelectImage(Path.Combine(AppContext.BaseDirectory, "Assets", "app_icon.png"), "Invalid image");
            h.Layout();
            var imageScreenshot = gpu.Render(h.Root);
            Png.EncodeToFile(Png.CreateRgba(gpu.Width, gpu.Height, imageScreenshot), Path.Combine(imageOutput, $"custom-image-{theme}-{scale}.png"));
        }

        void AssertPixel(byte[] pixels, float x, float y, uint expected)
        {
            var offset = ((gpu.Height - 1 - (int)(y * scale)) * gpu.Width + (int)(x * scale)) * 4;
            Assert.InRange(Math.Abs(pixels[offset] - (int)((expected >> 16) & 255)), 0, 15);
            Assert.InRange(Math.Abs(pixels[offset + 1] - (int)((expected >> 8) & 255)), 0, 15);
            Assert.InRange(Math.Abs(pixels[offset + 2] - (int)(expected & 255)), 0, 15);
        }
    }

    private static void Configure(Context ctx) => Configure(ctx, ThemeMode.Dark);
    private static void Configure(Context ctx, ThemeMode theme)
    {
        ctx.AddService<IThemeService<ThemeStyles>>(new ThemeService(new State<ThemeMode>(theme)));
        ctx.AddService<ILocalizationService>(new LocalizationService(new State<Locale>(Locale.En)));
        ctx.AddService<IClipboard>(new Clipboard());
    }

    private sealed class Clipboard : IClipboard
    {
        public string? GetText() => null;
        public void SetText(string text) { }
    }
}
