using ZGF.Gui;
using ZGF.Gui.Views;

namespace GitGui;

public sealed class ProgressBarView : RectView
{
    private readonly RectView _fill;
    private float _percent;

    public ProgressBarView()
    {
        BackgroundColor = 0xFF2A2C30;
        BorderRadius = BorderRadiusStyle.All(2);
        Height = 4f;

        _fill = new RectView
        {
            BackgroundColor = 0xFF4E8B3D,
            BorderRadius = BorderRadiusStyle.All(2),
        };
        Children.Add(_fill);
    }

    public float Percent
    {
        get => _percent;
        set
        {
            var clamped = value < 0f ? 0f : (value > 1f ? 1f : value);
            if (Math.Abs(_percent - clamped) < 0.001f) return;
            _percent = clamped;
            SetDirty();
        }
    }

    public uint FillColor
    {
        get => _fill.BackgroundColor;
        set => _fill.BackgroundColor = value;
    }

    protected override void OnLayoutChildren()
    {
        var pos = Position;
        var width = pos.Width * _percent;
        _fill.LeftConstraint = pos.Left;
        _fill.BottomConstraint = pos.Bottom;
        _fill.WidthConstraint = width;
        _fill.HeightConstraint = pos.Height;
        _fill.LayoutSelf();
    }
}
