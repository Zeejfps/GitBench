using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitGui;

internal sealed class SegmentView : MultiChildView, IBind<SegmentViewModel>
{
    private const float SegmentHeight = 28f;

    private readonly State<bool> _isActive = new(false);
    private readonly State<bool> _isHovered = new(false);

    private SegmentViewModel? _vm;

    public SegmentView(string label, BorderRadiusStyle cornerRadius)
    {
        Height = SegmentHeight;

        var labelView = new TextView
        {
            Text = label,
            VerticalTextAlignment = TextAlignment.Center,
            HorizontalTextAlignment = TextAlignment.Center,
        };
        labelView.BindThemedTextColor(s =>
            _isActive.Value ? s.ModeSwitcher.SegmentActiveText :
            _isHovered.Value ? s.ModeSwitcher.SegmentHoverText :
            s.ModeSwitcher.SegmentIdleText);

        var bg = new RectView
        {
            BorderRadius = cornerRadius,
            Padding = new PaddingStyle { Left = 12, Right = 12 },
            Children = { labelView },
        };
        bg.BindThemedBackgroundColor(s =>
            _isActive.Value ? s.ModeSwitcher.SegmentActiveBackground :
            _isHovered.Value ? s.ModeSwitcher.SegmentHoverBackground :
            s.ModeSwitcher.SegmentIdleBackground);
        AddChildToSelf(bg);

        this.UseController(_ => new HoverableButtonController(OnClicked, h => _isHovered.Value = h));
    }

    public void Bind(SegmentViewModel vm)
    {
        _vm = vm;
        vm.IsActive.Subscribe(b => _isActive.Value = b);
    }

    private void OnClicked() => _vm?.Activate();
}
