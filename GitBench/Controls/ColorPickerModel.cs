using System.Globalization;
using ZGF.Observable;

namespace GitBench.Controls;

/// <summary>Opaque sRGB color math; UI hue is retained when gray or black has no defined hue.</summary>
internal readonly record struct HsvColor(float Hue, float Saturation, float Value)
{
    public uint ToArgb()
    {
        var h = ((Hue % 360) + 360) % 360 / 60;
        var s = Math.Clamp(Saturation, 0, 1);
        var v = Math.Clamp(Value, 0, 1);
        uint Channel(float n)
        {
            var k = (n + h) % 6;
            return (uint)MathF.Round(255 * (v - v * s * Math.Max(0, Math.Min(Math.Min(k, 4 - k), 1))));
        }
        return 0xFF000000 | Channel(5) << 16 | Channel(3) << 8 | Channel(1);
    }

    public static HsvColor FromArgb(uint color, HsvColor previous = default)
    {
        var r = ((color >> 16) & 255) / 255f;
        var g = ((color >> 8) & 255) / 255f;
        var b = (color & 255) / 255f;
        var max = Math.Max(r, Math.Max(g, b));
        var delta = max - Math.Min(r, Math.Min(g, b));
        var hue = delta == 0 ? previous.Hue
            : ((max == r ? (g - b) / delta : max == g ? (b - r) / delta + 2 : (r - g) / delta + 4) * 60 + 360) % 360;
        return new(hue, max == 0 ? previous.Saturation : delta / max, max);
    }

    public static string Hex(uint color) => $"#{color & 0xFFFFFF:X6}";

    public static bool TryParseHex(string text, out uint color)
    {
        var hex = text.Trim();
        if (hex.StartsWith('#')) hex = hex[1..];
        color = 0;
        if (hex.Length is not (3 or 6) ||
            !uint.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var rgb)) return false;
        if (hex.Length == 3)
            rgb = ((rgb >> 8) & 15) * 17 << 16 | ((rgb >> 4) & 15) * 17 << 8 | (rgb & 15) * 17;
        color = rgb | 0xFF000000;
        return true;
    }

    public static uint Foreground(uint color)
    {
        static double Linear(uint channel)
        {
            var c = channel / 255d;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        var luminance = 0.2126 * Linear((color >> 16) & 255) + 0.7152 * Linear((color >> 8) & 255) + 0.0722 * Linear(color & 255);
        return luminance > 0.179 ? 0xFF000000 : 0xFFFFFFFF;
    }
}

/// <summary>A draft selection, independent of persistence. Null means use the supplied default.</summary>
internal sealed class ColorPickerModel : IDisposable
{
    private readonly State<uint?> _selected;
    private readonly State<HsvColor> _hsv;
    private readonly State<bool> _valid = new(true);
    private readonly IDisposable _hexSubscription;
    private bool _writingHex;

    public uint DefaultColor { get; }
    public IReadable<uint?> SelectedColor => _selected;
    public IReadable<HsvColor> Hsv => _hsv;
    public IReadable<bool> IsValid => _valid;
    public State<string> Hex { get; }
    public uint Color => _selected.Value ?? DefaultColor;

    public ColorPickerModel(uint? initialColor, uint defaultColor)
    {
        DefaultColor = defaultColor | 0xFF000000;
        _selected = new(initialColor is { } c ? c | 0xFF000000 : null);
        _hsv = new(HsvColor.FromArgb(Color));
        Hex = new(HsvColor.Hex(Color));
        _writingHex = true;
        _hexSubscription = Hex.Subscribe(text =>
        {
            if (_writingHex) return;
            _valid.Value = HsvColor.TryParseHex(text, out var color);
            if (!_valid.Value) return;
            _hsv.Value = HsvColor.FromArgb(color, _hsv.Value);
            _selected.Value = color;
        });
        _writingHex = false;
    }

    public void Select(uint color) => SetHsv(HsvColor.FromArgb(color, _hsv.Value));

    public void SetHsv(HsvColor hsv)
    {
        _hsv.Value = new(Math.Clamp(hsv.Hue, 0, 360), Math.Clamp(hsv.Saturation, 0, 1), Math.Clamp(hsv.Value, 0, 1));
        _selected.Value = _hsv.Value.ToArgb();
        WriteHex();
    }

    public void Reset()
    {
        _hsv.Value = HsvColor.FromArgb(DefaultColor, _hsv.Value);
        _selected.Value = null;
        WriteHex();
    }

    private void WriteHex()
    {
        _writingHex = true;
        try { Hex.Value = HsvColor.Hex(Color); }
        finally { _writingHex = false; }
        _valid.Value = true;
    }

    public void Dispose() => _hexSubscription.Dispose();
}
