using GitBench.App;
using GitBench.Controls;
using GitBench.Features.Assistant;
using GitBench.Localization;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Review.Walkthrough;

/// <summary>
/// The review window header's "Walk me through this": the dino mark and a label, asking the
/// built-in assistant to narrate the window's change. Absent while the assistant cannot answer —
/// no key resolved, or no assistant in this context at all — since a walkthrough nobody would
/// narrate is not worth a button; disabled while one is being started, since a second ask would
/// only queue behind the first.
/// </summary>
internal sealed record WalkthroughStartButton : Widget
{
    public const string ButtonId = "walkthrough-start";

    private const int MarkSize = 14;

    protected override IWidget Build(Context ctx)
    {
        var vm = ctx.Require<ReviewWindowViewModel>();
        var configured = ctx.Get<IAssistantSessionStore>()?.IsConfigured ?? new State<bool>(false);

        return new Show
        {
            When = configured,
            Then = () => new ButtonWidget
            {
                Id = ButtonId,
                Style = ButtonStyle.Outline(static s => s.Palette.TextBody),
                Command = new Command(vm.StartWalkthrough, new Derived<bool>(() => !vm.Walkthrough.IsStarting.Value)),
                Children =
                [
                    new AssistantMark { Size = MarkSize },
                    new ButtonLabel { Value = L.T(s => s.ReviewWalkthroughStart) },
                ],
            }
            .WithTooltip(L.T(s => s.ReviewWalkthroughStartTooltip))
            .WithController<KbmController>(),
        };
    }
}
