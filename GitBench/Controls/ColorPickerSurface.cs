using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.KeyboardModule;

namespace GitBench.Controls;

/// <summary>Cached gradient texture with an inset, high-contrast selection handle.</summary>
internal sealed class ColorPickerSurface(ColorPickerModel model, bool hueOnly) : View
{
    private readonly string _imageId = $"color-picker:{Guid.NewGuid():N}";
    private ICanvas? _canvas;
    private float _drawnHue = float.NaN;
    private bool _uploaded;
    private bool _focused;
    private (int Width, int Height) _imageSize;
    private const float Inset = 8;

    public RectF Track => new(Position.Left + Inset, Position.Bottom + Inset,
        Math.Max(1, Position.Width - 2 * Inset), Math.Max(1, Position.Height - 2 * Inset));

    public void Refresh() => MarkVisualDirty();
    public void SetFocused(bool focused) { _focused = focused; Refresh(); }
    public void Release()
    {
        if (_uploaded) _canvas?.RemoveImage(_imageId);
        _uploaded = false;
        _canvas = null;
    }

    protected override void OnDrawSelf(ICanvas canvas)
    {
        var hsv = model.Hsv.Value;
        if (!ReferenceEquals(_canvas, canvas)) { Release(); _canvas = canvas; }
        // DrawImage aspect-fits, so the texture must share the track's aspect ratio.
        var width = Math.Max(2, (int)Track.Width);
        var height = Math.Max(2, (int)Track.Height);
        if (!_uploaded || _imageSize != (width, height) || (!hueOnly && _drawnHue != hsv.Hue))
        {
            var pixels = new byte[width * height * 4];
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var color = hueOnly
                    ? new HsvColor(x * 360f / (width - 1), 1, 1).ToArgb()
                    : new HsvColor(hsv.Hue, x / (float)(width - 1), 1 - y / (float)(height - 1)).ToArgb();
                var offset = (y * width + x) * 4;
                pixels[offset] = (byte)(color >> 16);
                pixels[offset + 1] = (byte)(color >> 8);
                pixels[offset + 2] = (byte)color;
                pixels[offset + 3] = 255;
            }
            _uploaded = canvas.CreateOrUpdateRgbaImage(_imageId, width, height, pixels);
            _imageSize = (width, height);
            _drawnHue = hsv.Hue;
        }
        if (_uploaded)
            canvas.DrawImage(new DrawImageInputs { Position = Track, ImageId = _imageId, ZIndex = GetDrawZIndex(), TintColor = 0xFFFFFFFF, Rotation = 0 });

        var center = new PointF(Track.Left + Track.Width * (hueOnly ? hsv.Hue / 360 : hsv.Saturation),
            hueOnly ? Track.Center.Y : Track.Bottom + Track.Height * hsv.Value);
        var radius = _focused ? 7 : 5;
        canvas.DrawCircle(new DrawCircleInputs { Center = center, Radius = radius + 1, Color = 0xFF000000, ZIndex = GetDrawZIndex() });
        canvas.DrawCircle(new DrawCircleInputs { Center = center, Radius = radius, Color = 0xFFFFFFFF, ZIndex = GetDrawZIndex() });
        canvas.DrawCircle(new DrawCircleInputs { Center = center, Radius = radius - 2, Color = hueOnly ? new HsvColor(hsv.Hue, 1, 1).ToArgb() : model.Color, ZIndex = GetDrawZIndex() });
    }

    public void Pick(PointF point)
    {
        var x = Math.Clamp((point.X - Track.Left) / Track.Width, 0, 1);
        var y = Math.Clamp((point.Y - Track.Bottom) / Track.Height, 0, 1);
        model.SetHsv(hueOnly ? model.Hsv.Value with { Hue = x * 360 } : model.Hsv.Value with { Saturation = x, Value = y });
    }

    public void Adjust(KeyboardKey key, bool coarse)
    {
        var step = coarse ? 10 : 1;
        var hsv = model.Hsv.Value;
        var positive = key is KeyboardKey.RightArrow or KeyboardKey.UpArrow;
        if (hueOnly) hsv = hsv with { Hue = hsv.Hue + (positive ? step : -step) };
        else if (key is KeyboardKey.LeftArrow or KeyboardKey.RightArrow) hsv = hsv with { Saturation = hsv.Saturation + (positive ? step : -step) / 100f };
        else hsv = hsv with { Value = hsv.Value + (positive ? step : -step) / 100f };
        model.SetHsv(hsv);
    }
}

internal sealed class ColorPickerSurfaceController(ColorPickerSurface view, InputSystem input) : KeyboardMouseController
{
    private bool _dragging;
    private bool _focused;
    public Action? Next { get; set; }
    public Action? Previous { get; set; }
    public Action? Submit { get; set; }
    public Action? Cancel { get; set; }
    public override void OnFocusGained() { _focused = true; view.SetFocused(true); }
    public override void OnFocusLost() { _focused = _dragging = false; view.SetFocused(false); }

    public override void OnMouseButtonStateChanged(ref MouseButtonEvent e)
    {
        if (e.Phase != EventPhase.Bubbling || e.Button != MouseButton.Left) return;
        if (e.State == InputState.Pressed)
        {
            if (!view.Position.ContainsPoint(e.Mouse.Point)) { input.Blur(this); return; }
            input.StealFocus(this);
            _dragging = true;
            view.Pick(e.Mouse.Point);
            e.Consume();
        }
        else if (_dragging) { _dragging = false; view.Pick(e.Mouse.Point); e.Consume(); }
    }

    public override void OnMouseMoved(ref MouseMoveEvent e)
    {
        if (!_dragging) return;
        view.Pick(e.Mouse.Point);
        e.Consume();
    }

    public override void OnKeyboardKeyStateChanged(ref KeyboardKeyEvent e)
    {
        if (!_focused || e.Phase != EventPhase.Bubbling || e.State != InputState.Pressed) return;
        if (e.Key == KeyboardKey.Escape && Cancel is { } cancel)
        {
            cancel();
            e.Consume();
        }
        else if (e.Key is KeyboardKey.Enter or KeyboardKey.NumpadEnter && Submit is { } submit)
        {
            submit();
            e.Consume();
        }
        else if (e.Key == KeyboardKey.Tab)
        {
            if ((e.Modifiers & InputModifiers.Shift) != 0) Previous?.Invoke(); else Next?.Invoke();
            e.Consume();
        }
        else if (e.Key is KeyboardKey.LeftArrow or KeyboardKey.RightArrow or KeyboardKey.UpArrow or KeyboardKey.DownArrow)
        {
            view.Adjust(e.Key, (e.Modifiers & InputModifiers.Shift) != 0);
            e.Consume();
        }
    }
}
