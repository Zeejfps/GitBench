using GitBench.Controls;
using GitBench.Features.StatusBar;
using GitBench.Localization;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Notifications;

/// <summary>
/// One toast: a raised card with a severity-colored accent edge and icon, the message, an optional
/// action button, and a dismiss button. Severity maps to icon + accent here (a view concern); the
/// view model carries only the data.
/// </summary>
internal sealed record ToastCard : Widget
{
    private const float CardWidth = 340f;

    protected override IWidget Build(Context ctx)
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

        var close = new StatusBarIconButton
        {
            Icon = LucideIcons.X,
            Command = vm.Dismiss,
            BoxWidth = 18,
            BoxHeight = 18,
            IconSize = 12,
        }.WithTooltip(L.T(s => s.ToastDismiss)).WithController<KbmController>();

        IWidget[] rowChildren = vm.HasAction
            ? [icon, message, ActionButton(vm), close]
            : [icon, message, close];

        return new Box
        {
            Width = CardWidth,
            Background = Theme.Color(s => s.Palette.SurfaceRaised),
            BorderRadius = BorderRadiusStyle.All(Radius.Md),
            BorderSize = new BorderSizeStyle { Left = 3, Top = 1, Right = 1, Bottom = 1 },
            BorderColor = Theme.BorderColor(s => new BorderColorStyle
            {
                Left = accent(s),
                Top = s.Palette.Border,
                Right = s.Palette.Border,
                Bottom = s.Palette.Border,
            }),
            Shadow = Theme.Color(s => s.Palette.Shadow).Select(c => new BoxShadowStyle
            {
                Color = c,
                OffsetY = 2f,
                Blur = 12f,
            }),
            Children =
            [
                new Padding
                {
                    Amount = new PaddingStyle { Left = Spacing.Md, Right = Spacing.Sm, Top = Spacing.Sm, Bottom = Spacing.Sm },
                    Children =
                    [
                        new Row
                        {
                            Gap = Spacing.Sm,
                            CrossAxis = CrossAxisAlignment.Center,
                            Children = rowChildren,
                        },
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
