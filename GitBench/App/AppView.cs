using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Features.Branches;
using GitBench.Features.Diff;
using GitBench.Features.Operations;
using GitBench.Features.Repos;
using GitBench.Features.StatusBar;
using GitBench.Features.Submodules;
using GitBench.Features.Toolbar;
using GitBench.Messages;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.KeyboardModule;

namespace GitBench.App;

internal sealed record AppView : Widget
{
    protected override View CreateView(Context ctx)
    {
        var preferences = ctx.Require<PreferencesService>();
        var prefs = preferences.Current;

        var root = new ContainerView();

        // Full-width update banner stacked above the main layout. It self-hides (collapsing
        // its layout slot) until an update is staged, so the FlexColumn shows no residual bar.
        // Kept separate from the per-repo operations banner below.
        root.Children.Add(new FlexColumnView
        {
            CrossAxisAlignment = CrossAxisAlignment.Stretch,
            Children =
            {
                new UpdateBannerView().BuildView(ctx),
                new FlexItem
                {
                    Grow = 1,
                    // Wrapped in a ContainerView so it satisfies FlexItem.Child (BorderLayoutView
                    // is a plain View); the wrapper stretches the layout to fill the grow region.
                    Child = new ContainerView { Children = { new BorderLayoutView
                    {
                        West = ResizableLeftSidebar.Build(
                            ctx,
                            new RepoBar().BuildView(ctx),
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
                                new DetachedHeadBannerView().BuildView(ctx),
                                // Sits just below: warns when submodules are out of date with
                                // the recorded pointer and offers a one-click update. Also
                                // self-hides when everything's in sync.
                                new SubmoduleStatusBannerView().BuildView(ctx),
                                new FlexItem
                                {
                                    Grow = 1,
                                    // ContainerView wrapper stretches the BorderLayout to fill
                                    // the grow region (BorderLayoutView is a plain View).
                                    Child = new ContainerView { Children = { new BorderLayoutView
                                    {
                                        West = ResizableLeftSidebar.Build(
                                            ctx,
                                            new FlexColumnView
                                            {
                                                CrossAxisAlignment = CrossAxisAlignment.Stretch,
                                                Children =
                                                {
                                                    new BranchesHeader().BuildView(ctx),
                                                    new FlexItem { Grow = 1, Child = new BranchesView().BuildView(ctx) },
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
                                                    new OperationBannerView().BuildView(ctx),
                                                    new ActionsToolbar().BuildView(ctx),
                                                },
                                            },
                                            Center = new MainContentView(ctx),
                                        },
                                    } } },
                                },
                            },
                        },
                        South = new StatusBarView().BuildView(ctx),
                    } } },
                },
            },
        });
        root.Children.Add(new DragOverlay(ctx));

        var dialogSurfaceView = new DialogSurfaceView(ctx.Require<InputSystem>());
        root.Children.Add(dialogSurfaceView);

        root.Behaviors.Add(new DialogPresenter(ctx, dialogSurfaceView));

        // Headless host that materializes pop-out diff windows from DiffWindowsViewModel.
        root.Children.Add(new DiffWindowsView(ctx));

        root.UseController(ctx.Require<InputSystem>(), () => new AppKeybindController(
            ctx.Require<IRepoRegistry>(),
            ctx.Require<IMessageBus>()));

        return root;
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
