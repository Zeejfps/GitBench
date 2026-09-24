using GitBench.App;
using GitBench.Controls;
using GitBench.Features.Pairing;
using GitBench.Features.Repos;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Desktop.Components.ContextMenu;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Assistant;

/// <summary>
/// The assistant's entry point in the commit bar: the dino mark, opening a menu of the things worth
/// offering there — writing the commit message, and asking the agent to review the changes or to
/// chat.
/// </summary>
/// <remarks>
/// The menu opens upward: the commit bar sits at the bottom of the workspace, so a downward menu
/// would land off-window. While a message is being written the mark gives way to the same rotating
/// loader the Commit button uses, so the wait is visible without the menu open.
/// </remarks>
internal sealed record CommitAssistantButton : Widget<CommitAssistantButton.Spinner>
{
    private const int MarkSize = 16;

    // Addressed to the agent rather than read by anyone, so written here in English. What there is to
    // review is the agent's to work out: the uncommitted work and the branch's own commits are both
    // in scope, and either can be empty.
    private const string ReviewAsk =
        "Review my changes — what is uncommitted in the working tree, and what the checked-out "
        + "branch adds on top of its base.";

    protected override Spinner CreateState(Context ctx) =>
        new(ctx.Require<IFrameTicker>(), ctx.Require<AssistantViewModel>().IsGeneratingMessage);

    protected override IWidget Build(Context ctx, Spinner spinner)
    {
        var vm = ctx.Require<AssistantViewModel>();
        var chat = ctx.Require<AgentChat>();
        var loc = ctx.Localization();
        var busy = vm.IsGeneratingMessage;

        void AskAgents(RectF rect, Action<AgentConversation>? then = null) =>
            RepoBarContextMenu.Show(ctx, rect.TopLeft, chat.AgentMenu(then), MenuPlacement.Above);

        IReadOnlyList<RepoBarContextMenu.Item> Menu(RectF rect) =>
        [
            .. vm.BuildCommitMenu(),
            new RepoBarContextMenu.Item(
                loc.Strings.Value.AssistantReviewBranch,
                () =>
                {
                    if (chat.Ask(ReviewAsk, null) is AgentChatAsk.NeedsAgent)
                        AskAgents(rect, conversation => conversation.Say(ReviewAsk));
                },
                LucideIcons.Search,
                Enabled: chat.IsAvailable.Value),
            new RepoBarContextMenu.Item(
                loc.Strings.Value.AssistantChat,
                () =>
                {
                    if (chat.Reveal() is AgentChatPress.NeedsAgent) AskAgents(rect);
                },
                LucideIcons.Sparkles,
                Enabled: chat.IsAvailable.Value),
        ];

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
                RepoBarContextMenu.Show(ctx, rect.TopLeft, Menu(rect), MenuPlacement.Above));
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
