using GitBench.Controls;
using GitBench.Features.StatusBar;
using GitBench.Localization;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Desktop;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Desktop.Widgets;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Notifications;

/// <summary>
/// One toast, shaped to sit inside the status bar: a severity-colored glyph, the message, an
/// optional action, and — only for sticky toasts — a dismiss button. Fades and slides in from the
/// bar's trailing edge, and back out on dismiss. Severity maps to glyph + accent here (a view
/// concern); the view model carries only the data.
/// </summary>
internal sealed record ToastChip : Widget<ToastChipState>
{
    private const float ChipHeight = 18f;
    private const float GlyphSize = 12f;
    private const float SlideIn = 28f;

    // A long message (a failed patch apply) would otherwise run the width of the window and push the
    // action past the far edge; beyond this it ellipsizes.
    private const float MaxMessageWidth = 420f;

    protected override ToastChipState CreateState(Context ctx)
    {
        var vm = ctx.Require<ToastItemViewModel>();
        return new ToastChipState(ctx.Require<IFrameTicker>(), vm.Exiting);
    }

    protected override IWidget Build(Context ctx, ToastChipState state)
    {
        var vm = ctx.Require<ToastItemViewModel>();
        var accent = AccentFor(vm.Severity);

        var glyph = new Text
        {
            Value = IconFor(vm.Severity),
            FontFamily = LucideIcons.FontFamily,
            FontSize = GlyphSize,
            VAlign = TextAlignment.Center,
            Color = Theme.Color(accent),
        };

        var message = new Text
        {
            Value = vm.Message,
            FontSize = FontSize.Caption,
            VAlign = TextAlignment.Center,
            Wrap = TextWrap.NoWrap,
            Overflow = TextOverflow.Ellipsis,
            MaxWidth = MaxMessageWidth,
            Color = Theme.Color(s => s.Palette.TextPrimary),
        };

        var row = new List<IWidget> { glyph, message };
        if (vm.HasAction) row.Add(new ToastActionButton { Label = vm.ActionLabel, Command = vm.InvokeAction });
        if (vm.ShowDismiss) row.Add(DismissButton(vm));

        var chip = new Box
        {
            Height = ChipHeight,
            Background = Theme.Color(s => s.Palette.SurfaceRaised),
            BorderRadius = BorderRadiusStyle.All(Radius.Sm),
            BorderSize = BorderSizeStyle.All(1),
            BorderColor = Theme.BorderColor(s => BorderColorStyle.All(s.Palette.Border)),
            // Fade and slide in from the trailing edge, both render-only, so this never re-lays-out.
            // Opacity rides the raw linear progress (an even fade); the slide rides the eased progress
            // (decelerate into place), and its sign follows the writing direction — an RTL bar puts the
            // slot at the left edge, so the chip has to arrive from the left.
            Opacity = Prop.Bind(state.Enter.LinearProgress),
            TranslationX = Prop.Bind(() =>
                (Direction.IsRtl(ctx) ? -SlideIn : SlideIn) * (1f - state.Enter.Progress.Value)),
            Children =
            [
                new Padding
                {
                    Amount = new PaddingStyle { Left = Spacing.Sm, Right = Spacing.Xs },
                    Children =
                    [
                        new Row { Gap = Spacing.Sm, CrossAxis = CrossAxisAlignment.Center, Children = row.ToArray() },
                    ],
                },
            ],
        };

        // Clicking anywhere on the chip dismisses it. Inner buttons (action / dismiss) sit deeper in
        // the tree, so they still win their own clicks; only the body forwards here.
        return new KbmInput
        {
            Controller = _ => new ToastDismissController(vm.Dismiss),
            Child = chip,
        };
    }

    private static IWidget DismissButton(ToastItemViewModel vm) => new StatusBarIconButton
    {
        Icon = LucideIcons.X,
        Command = vm.Dismiss,
        BoxWidth = 16,
        BoxHeight = 16,
        IconSize = 11,
    }.WithTooltip(L.T(s => s.ToastDismiss)).WithController<KbmController>();

    // Info reuses the success glyph (no dedicated info glyph in the icon subset); the accent color
    // carries the distinction. Warning and Error share the alert glyph.
    private static string IconFor(ToastSeverity severity) => severity switch
    {
        ToastSeverity.Success => LucideIcons.CircleCheck,
        ToastSeverity.Info => LucideIcons.CircleCheck,
        ToastSeverity.Warning => LucideIcons.TriangleAlert,
        ToastSeverity.Error => LucideIcons.TriangleAlert,
        _ => LucideIcons.CircleCheck,
    };

    private static Func<ThemeStyles, uint> AccentFor(ToastSeverity severity) => severity switch
    {
        ToastSeverity.Success => static s => s.Status.Success,
        ToastSeverity.Info => static s => s.Status.Info,
        ToastSeverity.Warning => static s => s.Status.Warning,
        ToastSeverity.Error => static s => s.Status.Danger,
        _ => static s => s.Status.Info,
    };

    // Dismisses the toast on a left click anywhere on the chip body, and shows a hand cursor while
    // hovered so the chip reads as clickable. Mirrors the framework's semantic click (left press on
    // the bubble phase) and consumes it. Dismiss is idempotent, so an inner button that also dismisses
    // (or lets the click bubble) causes no double-dismissal.
    private sealed class ToastDismissController : KeyboardMouseController, IProvidesCursor
    {
        private readonly ICommand _dismiss;

        public ToastDismissController(ICommand dismiss) => _dismiss = dismiss;

        public MouseCursor Cursor => MouseCursor.Hand;

        public override void OnMouseButtonStateChanged(ref MouseButtonEvent e)
        {
            if (e.Phase != EventPhase.Bubbling) return;
            if (e.Button != MouseButton.Left) return;
            if (e.State != InputState.Pressed) return;
            _dismiss.Execute();
            e.Consume();
        }
    }
}

/// <summary>
/// A toast's action (Undo / Retry) as a status-bar-sized text button — the chip has room for a label
/// and a hover wash, not the full control-height button chrome.
/// </summary>
internal sealed record ToastActionButton : Widget<ButtonState>
{
    public required string Label { get; init; }
    public required ICommand Command { get; init; }

    protected override ButtonState CreateState(Context ctx) => new(Command);

    protected override IWidget Build(Context ctx, ButtonState state) => new Box
    {
        BorderRadius = BorderRadiusStyle.All(Radius.Sm),
        Background = Theme.Color(s => s.StatusBar.IconButtonBackground(state)),
        Children =
        [
            new Padding
            {
                Amount = new PaddingStyle { Left = Spacing.Xs, Right = Spacing.Xs },
                Children =
                [
                    new Text
                    {
                        Value = Label,
                        FontSize = FontSize.Caption,
                        VAlign = TextAlignment.Center,
                        Color = Theme.Color(s => state.Hovered.Value ? s.Palette.AccentHover : s.Palette.Accent),
                    },
                ],
            },
        ],
    };
}

/// <summary>
/// Per-chip animation state (auto-disposed on unmount): the enter fade/slide tween, reversed on
/// dismiss to fade/slide out. Stops ticking once finished.
/// </summary>
internal sealed class ToastChipState : IDisposable
{
    public Tween Enter { get; }
    private readonly IDisposable _exitSub;

    public ToastChipState(IFrameTicker ticker, IReadable<bool> exiting)
    {
        Enter = new Tween(ticker, 0.3f, Easings.EaseOutCubic);
        Enter.Play();

        // Run the enter tween backwards (fade + slide out) once the toast is dismissed. Subscribe
        // fires immediately with the current value (false → no-op), then again when it flips true.
        _exitSub = exiting.Subscribe(v => { if (v) Enter.Reverse(); });
    }

    public void Dispose()
    {
        _exitSub.Dispose();
        Enter.Dispose();
    }
}
