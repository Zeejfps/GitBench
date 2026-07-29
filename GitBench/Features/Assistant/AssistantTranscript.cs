using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Components.Controls;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Assistant;

/// <summary>
/// The scrolling conversation: the active repository's rows, a resting hint while there are none,
/// and the thinking placeholder while a turn is still reasoning. Place it in a <c>Grow</c> — it
/// reports no intrinsic height and takes the slot it is handed.
/// </summary>
/// <remarks>
/// Sticks to the bottom so a streamed answer stays in view as it is written, but only while the
/// reader is already there — scrolling up to re-read something is not interrupted by the reply
/// still arriving.
/// </remarks>
internal sealed record AssistantTranscript : Widget
{
    protected override IWidget Build(Context ctx)
    {
        var vm = ctx.Require<AssistantViewModel>();

        return new ScrollArea
        {
            FillParent = true,
            AutoHide = true,
            StickToBottom = true,
            WheelStep = Scrolling.WheelStep,
            Style = Theme.ScrollBar(),
            Children =
            [
                new Padding
                {
                    Amount = PaddingStyle.All(Spacing.Lg),
                    Children =
                    [
                        new Column
                        {
                            Gap = Spacing.Lg,
                            CrossAxis = CrossAxisAlignment.Stretch,
                            Children =
                            [
                                new Text
                                {
                                    Value = L.T(s => s.AssistantEmptyHint),
                                    Visible = Prop.Bind(vm.IsEmpty),
                                    Wrap = TextWrap.Wrap,
                                    FontSize = FontSize.Body,
                                    Color = Theme.Color(s => s.Palette.TextMuted),
                                },
                                // Keyed on the session, so switching repositories swaps the whole
                                // transcript rather than reconciling one repo's rows into another's.
                                new Switch<AssistantSession?>
                                {
                                    Value = vm.Session,
                                    Case = session => session is null
                                        ? Empty.Widget
                                        : new Each<AssistantRow>
                                        {
                                            Items = session.Rows,
                                            Template = new TranscriptRow(),
                                            Gap = Spacing.Lg,
                                            CrossAxis = CrossAxisAlignment.Stretch,
                                        },
                                },
                                new Show
                                {
                                    When = vm.IsThinking,
                                    Then = static () => new AssistantThinkingIndicator(),
                                },
                            ],
                        },
                    ],
                },
            ],
        };
    }
}
