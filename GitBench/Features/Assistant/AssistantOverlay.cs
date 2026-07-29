using GitBench.Controls;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Assistant;

/// <summary>
/// Where the assistant sits over the workspace: floating where the user last left it, above the
/// content but deliberately without a scrim — the app behind it stays live and clickable while the
/// conversation is open.
/// </summary>
/// <remarks>
/// Placement and entrance only; the surface itself is <see cref="AssistantPanel"/>, so a docked or
/// windowed placement later composes the same panel.
///
/// Non-modal is about what happens outside the panel. Inside it the panel owns the pointer
/// (<see cref="SurfacePointerBlocker"/>) — without that, a drag or a wheel over it reaches the diff
/// underneath, because hit-testing only sees views that carry a controller.
/// </remarks>
internal sealed record AssistantOverlay : Widget
{
    public const string PanelId = "assistant-overlay";

    // Below the toast layer, above everything else: a toast is a moment, the overlay is a session.
    private const int Layer = 400;

    protected override IWidget Build(Context ctx)
    {
        var vm = ctx.Require<AssistantViewModel>();

        return new Stack
        {
            ZIndex = Layer,
            Children =
            [
                new Show
                {
                    When = vm.IsOpen,
                    Then = static () => new AssistantPanelFrame(),
                },
            ],
        };
    }
}

/// <summary>The panel's raised surface and its claim on the pointer. Takes the size the frame around
/// it hands over, so resizing is the placement's business rather than the card's.</summary>
internal sealed record AssistantOverlayCard : Widget
{
    protected override IWidget Build(Context ctx) =>
        new Box
        {
            Id = AssistantOverlay.PanelId,
            Background = Theme.Color(s => s.Palette.SurfaceRaised),
            BorderRadius = BorderRadiusStyle.All(Radius.Lg),
            BorderSize = BorderSizeStyle.All(1),
            BorderColor = Theme.BorderColor(s => BorderColorStyle.All(s.Palette.Border)),
            Shadow = Theme.Color(s => s.Palette.Shadow).Select(c => new BoxShadowStyle
            {
                Color = c,
                OffsetY = 6f,
                Blur = 24f,
            }),
            Children = [new AssistantPanel()],
        }
        .WithController(ctx.Require<InputSystem>(), static () => new SurfacePointerBlocker());
}
