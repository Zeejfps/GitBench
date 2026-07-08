using GitBench.Controls;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Review;

/// <summary>
/// The header's By-increment / Combined segmented toggle: two pills in a bordered group, the active one
/// taking the shared row-selection fill. Drives <see cref="ReviewWindowViewModel.SetMode"/>; visible
/// only once a stack is loaded (there's nothing to toggle while loading or on an empty range).
/// </summary>
internal sealed record ReviewModeToggle : Widget
{
    protected override IWidget Build(Context ctx)
    {
        var vm = ctx.Require<ReviewWindowViewModel>();

        return new Box
        {
            Visible = Prop.Bind(() => vm.ContentKind.Value == ReviewContentKind.Loaded),
            BorderRadius = BorderRadiusStyle.All(Radius.Sm),
            BorderSize = BorderSizeStyle.All(1),
            BorderColor = Theme.BorderColor(s => BorderColorStyle.All(s.Palette.Border)),
            Children =
            [
                new Row
                {
                    CrossAxis = CrossAxisAlignment.Stretch,
                    Children =
                    [
                        Segment(ctx, vm, ReviewDiffMode.ByIncrement, L.T(s => s.ReviewModeByIncrement)),
                        Segment(ctx, vm, ReviewDiffMode.Combined, L.T(s => s.ReviewModeCombined)),
                    ],
                },
            ],
        };
    }

    private static IWidget Segment(Context ctx, ReviewWindowViewModel vm, ReviewDiffMode mode, Prop<string> label)
    {
        var input = ctx.Require<InputSystem>();
        var theme = ctx.Theme();
        var hover = new State<bool>(false);

        bool IsActive() => vm.Mode.Value == mode;

        var pill = new Box
        {
            Background = Prop.Bind(() =>
            {
                var sel = theme.Styles.Value.RowSelection;
                if (IsActive()) return sel.Fill;
                return hover.Value ? sel.FillHover : 0u;
            }),
            Children =
            [
                new Padding
                {
                    Amount = new PaddingStyle { Left = Spacing.Md, Right = Spacing.Md, Top = Spacing.Xs, Bottom = Spacing.Xs },
                    Children =
                    [
                        new Text
                        {
                            Value = label,
                            FontSize = FontSize.Caption,
                            VAlign = TextAlignment.Center,
                            Color = Prop.Bind(() => IsActive()
                                ? theme.Styles.Value.Palette.TextPrimary
                                : theme.Styles.Value.Palette.TextSecondary),
                        },
                    ],
                },
            ],
        };

        return pill.WithController(input, () => new SegmentClickController(hover, () => vm.SetMode(mode)));
    }
}

// Hover tracking + left-click selection for one toggle segment. Selection fires on release, but only
// when the press armed on this segment.
internal sealed class SegmentClickController : KeyboardMouseController
{
    private readonly State<bool> _hover;
    private readonly Action _onClick;
    private bool _armed;

    public SegmentClickController(State<bool> hover, Action onClick)
    {
        _hover = hover;
        _onClick = onClick;
    }

    public override void OnMouseEnter(ref MouseEnterEvent e) => _hover.Value = true;

    public override void OnMouseExit(ref MouseExitEvent e)
    {
        _hover.Value = false;
        _armed = false;
    }

    public override void OnMouseButtonStateChanged(ref MouseButtonEvent e)
    {
        if (e.Phase != EventPhase.Bubbling) return;
        if (e.Button != MouseButton.Left) return;

        if (e.State == InputState.Pressed)
        {
            _armed = true;
            e.Consume();
            return;
        }

        if (e.State != InputState.Released || !_armed) return;
        _armed = false;
        _onClick();
        e.Consume();
    }
}
