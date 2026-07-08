using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Identity;

// The profile-manager's delete confirmation, rendered as a nested popup inside the manager's own tree
// (the app dialog surface replaces rather than stacks, so a second modal there would blow the manager
// away). A dimmed, input-blocking backdrop covers the whole manager card with a small confirm card
// centered on top; both fade in while the card scales up, mirroring the top-level dialog entrance.
// Mounted only while a delete is armed (via Show), so the entrance replays on each open.
internal sealed record IdentityDeleteConfirmPopup : Widget
{
    // Matches DialogSurfaceView's top-level dialog entrance.
    private const float EnterScale = 0.94f;
    private const float EnterSeconds = 0.20f;

    public required IdentityProfileManagerDialogViewModel Vm { get; init; }
    public required Strings S { get; init; }

    protected override View CreateView(Context ctx)
    {
        var input = ctx.Require<InputSystem>();
        var ticker = ctx.Require<IFrameTicker>();

        var cardView = Card(ctx).BuildView(ctx);

        // Backdrop and the centered card are siblings, not parent/child: the input-blocker lives on the
        // backdrop only, so it swallows clicks on the dimmed area without also eating the card's buttons
        // (a blocker on an ancestor of the buttons would consume their press during the capture phase).
        var backdrop = new RectView
        {
            BackgroundColor = 0xB0000000,
            BorderRadius = BorderRadiusStyle.All(DialogFrame.DefaultBorderRadius),
        };
        backdrop.UseController(input, () => new DialogInputBlockingController());

        var root = new ContainerView
        {
            Children =
            {
                backdrop,
                new FlexColumnView
                {
                    MainAxisAlignment = MainAxisAlignment.Center,
                    CrossAxisAlignment = CrossAxisAlignment.Center,
                    Children = { cardView },
                },
            },
        };

        // Backdrop + card fade on the raw progress (an even fade); the card scales up on the eased
        // progress so it grows into place, both parking once landed so idle frames stop.
        var tween = new Tween(ticker, EnterSeconds, Easings.EaseOutCubic);
        root.Bind(tween.LinearProgress, p =>
        {
            backdrop.Opacity = p;
            cardView.Opacity = p;
        });
        root.Bind(tween.Progress, p =>
        {
            var scale = EnterScale + (1f - EnterScale) * p;
            cardView.ScaleX = scale;
            cardView.ScaleY = scale;
        });
        root.Use(() => tween);
        tween.Play();
        return root;
    }

    private IWidget Card(Context ctx)
    {
        var theme = ctx.Theme();

        var cancel = new SecondaryDialogButton
        {
            Label = S.CommonCancel,
            Command = new ZGF.Observable.Command(Vm.CancelDelete),
            Height = DialogFrame.DefaultButtonHeight,
            MinWidth = DialogFrame.DefaultButtonMinWidth,
        }.WithController<KbmController>();

        var confirm = new ActionDialogButton
        {
            Label = S.CommonDelete,
            Role = DialogButtonRole.Destructive,
            Command = new ZGF.Observable.Command(Vm.ConfirmDelete),
            Height = DialogFrame.DefaultButtonHeight,
            MinWidth = DialogFrame.DefaultButtonMinWidth,
        }.WithController<KbmController>();

        return new Box
        {
            Width = 400f,
            BorderSize = BorderSizeStyle.All(1),
            BorderRadius = BorderRadiusStyle.All(DialogFrame.DefaultBorderRadius),
            Background = Theme.Color(t => t.DialogFrame.Background),
            BorderColor = Theme.BorderColor(t => BorderColorStyle.All(t.DialogFrame.Border)),
            Shadow = Prop.Bind(() => new BoxShadowStyle
            {
                OffsetX = 0f,
                OffsetY = 8f,
                Blur = 24f,
                Spread = 0f,
                Color = theme.Styles.Value.DialogFrame.Shadow,
            }),
            Children =
            [
                new Padding
                {
                    Amount = PaddingStyle.All(DialogFrame.DefaultPadding),
                    Children =
                    [
                        new Column
                        {
                            Gap = Spacing.Md,
                            CrossAxis = CrossAxisAlignment.Center,
                            Children =
                            [
                                new Text
                                {
                                    Value = LucideIcons.TriangleAlert,
                                    FontFamily = LucideIcons.FontFamily,
                                    FontSize = 28f,
                                    HAlign = TextAlignment.Center,
                                    Color = Theme.Color(t => t.DialogFrame.ErrorText),
                                },
                                new Text
                                {
                                    Value = Prop.Bind<string?>(() => S.IdentityManageDeleteConfirm(Vm.PendingDeleteName.Value)),
                                    Weight = FontWeight.Bold,
                                    HAlign = TextAlignment.Center,
                                    Color = Theme.Color(t => t.Palette.TextStrong),
                                },
                                new Text
                                {
                                    Value = S.IdentityManageDeleteConfirmBody,
                                    Wrap = TextWrap.Wrap,
                                    HAlign = TextAlignment.Center,
                                    Color = Theme.Color(t => t.Palette.TextSecondary),
                                },
                                new Row
                                {
                                    Gap = Spacing.Sm,
                                    CrossAxis = CrossAxisAlignment.Center,
                                    Children = [cancel, confirm],
                                },
                            ],
                        },
                    ],
                },
            ],
        };
    }
}
