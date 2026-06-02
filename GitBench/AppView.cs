using ZGF.Gui;
using ZGF.Gui.Desktop;
using ZGF.Gui.Views;
using ZGF.KeyboardModule;

namespace GitGui;

public sealed class AppView : MultiChildView
{
    public AppView(PreferencesService preferences, UpdateService updateService)
    {
        var prefs = preferences.Current;
        // Full-width update banner stacked above the main layout. It self-hides (collapsing
        // its layout slot) until an update is staged, so the FlexColumn shows no residual bar.
        // Kept separate from the per-repo operations banner below.
        Children.Add(new FlexColumnView
        {
            CrossAxisAlignment = CrossAxisAlignment.Stretch,
            Children =
            {
                new UpdateBannerView(updateService),
                new FlexItem
                {
                    Grow = 1,
                    // Wrapped in a MultiChildView so it satisfies FlexItem.Child (BorderLayoutView
                    // is a plain View); the wrapper stretches the layout to fill the grow region.
                    Child = new MultiChildView { Children = { new BorderLayoutView
                    {
                        West = ResizableLeftSidebar.Build(
                            new RepoBar(),
                            initialWidth: prefs.RepoBarWidth,
                            minWidth: 220f,
                            onWidthChanged: preferences.SetRepoBarWidth),
                        Center = new FlexColumnView
                        {
                            CrossAxisAlignment = CrossAxisAlignment.Stretch,
                            Children =
                            {
                                // Repo-level detached-HEAD warning, stacked above both the
                                // branches sidebar and the main content so it's visible on any
                                // tab. Self-hides (collapsing its slot) when nothing's at risk.
                                new DetachedHeadBannerView(),
                                // Sits just below: warns when submodules are out of date with
                                // the recorded pointer and offers a one-click update. Also
                                // self-hides when everything's in sync.
                                new SubmoduleStatusBannerView(),
                                new FlexItem
                                {
                                    Grow = 1,
                                    // MultiChildView wrapper stretches the BorderLayout to fill
                                    // the grow region (BorderLayoutView is a plain View).
                                    Child = new MultiChildView { Children = { new BorderLayoutView
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
                                    } } },
                                },
                            },
                        },
                        South = new StatusBarView(),
                    } } },
                },
            },
        });
        Children.Add(new DragOverlay());

        var dialogSurfaceView = new DialogSurfaceView();
        Children.Add(dialogSurfaceView);

        Behaviors.Add(new DialogPresenter(dialogSurfaceView));

        // Headless host that materializes pop-out diff windows from DiffWindowsViewModel.
        Children.Add(new DiffWindowsView());

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
