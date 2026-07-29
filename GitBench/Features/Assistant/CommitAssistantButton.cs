using GitBench.App;
using GitBench.Controls;
using GitBench.Features.Repos;
using GitBench.Localization;
using ZGF.Gui;
using ZGF.Gui.Desktop.Components.ContextMenu;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Assistant;

/// <summary>
/// The assistant's entry point in the commit bar: the dino mark, opening a menu of the two things
/// worth offering there — writing the commit message, and the chat.
/// </summary>
/// <remarks>
/// The menu opens upward: the commit bar sits at the bottom of the workspace, so a downward menu
/// would land off-window. While a message is being written the mark gives way to the same rotating
/// loader the Commit button uses, so the wait is visible without the menu open.
/// </remarks>
internal sealed record CommitAssistantButton : Widget<CommitAssistantButton.Spinner>
{
    private const int MarkSize = 16;

    protected override Spinner CreateState(Context ctx) =>
        new(ctx.Require<IFrameTicker>(), ctx.Require<AssistantViewModel>().IsGeneratingMessage);

    protected override IWidget Build(Context ctx, Spinner spinner)
    {
        var vm = ctx.Require<AssistantViewModel>();
        var busy = vm.IsGeneratingMessage;

        var button = new ButtonWidget
        {
            // The press belongs to the menu controller below; this satisfies the button's command.
            Command = new Command(static () => { }),
            ContentInset = ButtonStyle.Plain.IconOnlyInset,
            Children =
            [
                new Show
                {
                    When = busy,
                    Then = () => new ButtonIcon
                    {
                        Value = LucideIcons.Loader,
                        Rotation = Prop.Bind(spinner.Rotation),
                    },
                    Else = () => new AssistantMark { Size = MarkSize },
                },
            ],
        };

        return button
            .WithTooltip(L.T(s => s.AssistantCommitMenuTooltip))
            .WithMenuController(rect =>
                RepoBarContextMenu.Show(ctx, rect.TopLeft, vm.BuildCommitMenu(), MenuPlacement.Above));
    }

    /// Keeps the loader turning for exactly as long as a commit message is being written.
    internal sealed class Spinner : IDisposable
    {
        private readonly SpinnerAnimation _animation;
        private readonly IDisposable _subscription;

        public Spinner(IFrameTicker ticker, IReadable<bool> busy)
        {
            _animation = new SpinnerAnimation(ticker);
            _subscription = busy.Subscribe(running =>
            {
                if (running) _animation.Start();
                else _animation.Stop();
            });
        }

        public IReadable<float> Rotation => _animation.Rotation;

        public void Dispose()
        {
            _subscription.Dispose();
            _animation.Dispose();
        }
    }
}
