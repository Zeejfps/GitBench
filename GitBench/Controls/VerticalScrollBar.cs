using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Components.VerticalScrollBar;
using ZGF.Gui.Desktop.Widgets;
using ZGF.Gui.Views;

namespace GitBench.Controls;

/// <summary>
/// The framework <see cref="ScrollBar"/> widget in view form, for the view-era hosts that place a
/// bar in a <see cref="BorderLayoutView"/> slot and sync it by hand. Hidden (reserving no gutter)
/// while the content it tracks fits; see <see cref="ScrollBarSync"/>.
/// </summary>
internal sealed class VerticalScrollBar : ContainerView
{
    private readonly VerticalScrollBarThumbView _thumb = new();

    public VerticalScrollBar(Context ctx)
    {
        Width = ScrollBarSync.Thickness;
        Children.Add(new ScrollBar { Thumb = _thumb, Style = Theme.ScrollBar() }.BuildView(ctx));
    }

    public event Action<float>? ScrollPositionChanged
    {
        add => _thumb.ScrollPositionChanged += value;
        remove => _thumb.ScrollPositionChanged -= value;
    }

    public float Scale
    {
        set => _thumb.Scale = value;
    }

    public void SetNormalizedScrollPosition(float normalized) => _thumb.SetScrollPositionNormalized(normalized);
}
