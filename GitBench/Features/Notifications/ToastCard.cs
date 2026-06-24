using GitBench.Controls;
using GitBench.Features.StatusBar;
using GitBench.Localization;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Notifications;

/// <summary>
/// One toast: a compact, content-sized card with a severity-colored icon, the message, an optional
/// action, and — only for sticky toasts — a dismiss button. Fades and rises in on appear, fades and
/// sinks out on dismiss. Severity maps to icon + accent here (a view concern); the view model
/// carries only the data.
/// </summary>
internal sealed record ToastCard : Widget<ToastCardState>
{
    private const float MinCardWidth = 160f;
    private const float SlideUp = 10f;

    protected override ToastCardState CreateState(Context ctx)
    {
        var vm = ctx.Require<ToastItemViewModel>();
        return new ToastCardState(ctx.Require<IFrameTicker>(), vm.Exiting);
    }

    protected override IWidget Build(Context ctx, ToastCardState state)
    {
        var vm = ctx.Require<ToastItemViewModel>();
        var accent = AccentFor(vm.Severity);

        var icon = new Text
        {
            Value = IconFor(vm.Severity),
            FontFamily = LucideIcons.FontFamily,
            FontSize = FontSize.Default,
            Width = 18,
            HAlign = TextAlignment.Center,
            VAlign = TextAlignment.Center,
            Color = Theme.Color(accent),
        };

        var message = new Grow
        {
            Child = new Text
            {
                Value = vm.Message,
                VAlign = TextAlignment.Center,
                Wrap = TextWrap.Wrap,
                Color = Theme.Color(s => s.Palette.TextPrimary),
            },
        };

        var row = new List<IWidget> { icon, message };
        if (vm.HasAction) row.Add(ActionButton(vm));
        if (vm.ShowDismiss) row.Add(DismissButton(vm));

        return new Box
        {
            MinWidth = MinCardWidth,
            Background = Theme.Color(s => s.Palette.SurfaceRaised),
            BorderRadius = BorderRadiusStyle.All(Radius.Md),
            BorderSize = BorderSizeStyle.All(1),
            BorderColor = Theme.BorderColor(s => BorderColorStyle.All(s.Palette.Border)),
            Shadow = Theme.Color(s => s.Palette.Shadow).Select(c => new BoxShadowStyle
            {
                Color = c,
                OffsetY = 4f,
                Blur = 16f,
            }),
            // Fade and rise into place; both are render-only, so this never re-lays-out. Opacity
            // rides the raw linear progress (an even fade); the slide rides the eased progress
            // (decelerates into place).
            Opacity = Prop.Bind(state.Enter.LinearProgress),
            TranslationY = state.Enter.Progress.Bind(p => -SlideUp * (1f - p)),
            Children =
            [
                new Padding
                {
                    Amount = new PaddingStyle { Left = Spacing.Md, Right = Spacing.Sm, Top = Spacing.Sm, Bottom = Spacing.Sm },
                    Children =
                    [
                        new Row { Gap = Spacing.Sm, CrossAxis = CrossAxisAlignment.Center, Children = row.ToArray() },
                    ],
                },
            ],
        };
    }

    private static IWidget ActionButton(ToastItemViewModel vm) => new ButtonWidget
    {
        Command = vm.InvokeAction,
        Children = [new ButtonLabel { Value = vm.ActionLabel }],
    }.WithController<KbmController>();

    private static IWidget DismissButton(ToastItemViewModel vm) => new StatusBarIconButton
    {
        Icon = LucideIcons.X,
        Command = vm.Dismiss,
        BoxWidth = 18,
        BoxHeight = 18,
        IconSize = 12,
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
}

/// <summary>
/// Per-card animation state (auto-disposed on unmount): the enter fade/slide tween, reversed on
/// dismiss to fade/slide out. Stops ticking once finished.
/// </summary>
internal sealed class ToastCardState : IDisposable
{
    public Tween Enter { get; }
    private readonly IDisposable _exitSub;

    public ToastCardState(IFrameTicker ticker, IReadable<bool> exiting)
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
