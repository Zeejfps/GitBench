using GitBench.Controls;
using GitBench.Features.LocalChanges;
using GitBench.Features.Operations;
using GitBench.Features.Toolbar;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.App;

internal sealed record WorkspaceWidget : Widget
{
    protected override IWidget Build(Context ctx) => new BorderLayout
    {
        West = new BranchesSidebar(),
        Center = new BorderLayout
        {
            North = new ActionsToolbar(),
            Center = new MainContent(),
            South = Footer(ctx),
        },
    };

    // The workspace footer, shared across the History and Local Changes tabs: the merge bar (for an
    // operation that carries a commit box — merge / unmerged paths) above the operation panel (the
    // sequencer operations). Both self-hide on their own conditions, so at most one is ever shown.
    private static IWidget Footer(Context ctx)
    {
        var vm = ctx.Require<LocalChangesViewModel>();
        var operation = ctx.Require<OperationViewModel>();
        var mergeActive = new Derived<bool>(() => operation.ShowsCommitBox.Value && operation.IsActive.Value);
        return new Column
        {
            CrossAxis = CrossAxisAlignment.Stretch,
            Children =
            [
                new CommitBarWidget
                {
                    FocusRing = new FocusRing(),
                    Vm = vm,
                    Active = mergeActive,
                    ShowOperationChrome = true,
                    Visible = Prop.Bind(mergeActive),
                },
                new OperationPanelWidget(),
            ],
        };
    }
}
