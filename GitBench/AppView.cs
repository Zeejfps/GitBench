using ZGF.Gui;
using ZGF.Gui.Desktop;
using ZGF.Gui.Views;
using ZGF.KeyboardModule;

namespace GitGui;

public sealed class AppView : MultiChildView
{
    public AppView(PreferencesService preferences)
    {
        var prefs = preferences.Current;
        Children.Add(new BorderLayoutView
        {
            West = ResizableLeftSidebar.Build(
                new RepoBar(),
                initialWidth: prefs.RepoBarWidth,
                minWidth: 220f,
                onWidthChanged: preferences.SetRepoBarWidth),
            Center = new BorderLayoutView
            {
                West = ResizableLeftSidebar.Build(
                    new FlexColumnView
                    {
                        CrossAxisAlignment = CrossAxisAlignment.Stretch,
                        Children =
                        {
                            new BranchesHeader(),
                            new FlexItem { Grow = 1, Child = new BranchesView() },
                        },
                    },
                    initialWidth: prefs.BranchesWidth,
                    onWidthChanged: preferences.SetBranchesWidth),
                Center = new BorderLayoutView
                {
                    North = new FlexColumnView
                    {
                        CrossAxisAlignment = CrossAxisAlignment.Stretch,
                        Children =
                        {
                            new OperationBannerView(),
                            new ActionsToolbar(),
                        },
                    },
                    Center = new MainContentView(),
                },
            },
        });
        Children.Add(new DragOverlay());

        var dialogSurfaceView = new DialogSurfaceView();
        Children.Add(dialogSurfaceView);

        Behaviors.Add(new DialogPresenter(dialogSurfaceView));

        this.UseController(ctx => new AppKeybindController(
            ctx.Require<IRepoRegistry>(),
            ctx.Require<IMessageBus>()));
    }
}

// Window-level keyboard shortcuts. Sits at the app root so the hover-built focus queue
// always includes it; descendant text inputs let F5 fall through (they only consume
// printable keys / editing shortcuts), so the refresh fires regardless of focus.
internal sealed class AppKeybindController : KeyboardMouseController
{
    private readonly IRepoRegistry _registry;
    private readonly IMessageBus _bus;

    public AppKeybindController(IRepoRegistry registry, IMessageBus bus)
    {
        _registry = registry;
        _bus = bus;
    }

    public override void OnKeyboardKeyStateChanged(ref KeyboardKeyEvent e)
    {
        if (e.State != InputState.Pressed) return;
        if (e.Key != KeyboardKey.F5) return;

        var repo = _registry.Active.Value;
        if (repo == null) return;

        // Replays the two bus messages that every refresh-on-change subscriber already
        // reacts to (CommitsPresenter, LocalChangesViewModel, BranchesViewModel,
        // ActionsToolbarViewModel, …). Equivalent to "pretend something just changed",
        // which is exactly what a forced refresh is.
        _bus.Broadcast(new RefsChangedMessage(repo.Id));
        _bus.Broadcast(new WorkingTreeChangedMessage(repo.Id));
        e.Consume();
    }
}
