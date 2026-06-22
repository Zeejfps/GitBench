using GitBench.Controls;
using GitBench.Features.LocalChanges;
using GitBench.Git;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Diff;

/// <summary>
/// The diff body itself: a virtualized, scrollable view of a <see cref="DiffResult"/> with
/// inline per-hunk Stage/Unstage/Discard. It is intentionally headerless — chrome lives in
/// the surrounding context: <see cref="DiffPaneHeaderWidget"/> for the embedded panes (Local
/// Changes, Commit Details) and <see cref="DiffWindowToolbar"/> for the pop-out window.
///
/// When the selected file is a conflicted (unmerged) working-tree file, the body swaps from
/// the diff to a <see cref="ConflictResolveView"/> resolution header.
/// </summary>
internal sealed record DiffView : Widget
{
    protected override IWidget Build(Context ctx)
    {
        var vm = ctx.Require<DiffViewModel>();

        var content = new DiffContentView(ctx)
        {
            OnStageHunk = vm.StageHunk,
            OnUnstageHunk = vm.UnstageHunk,
            OnDiscardHunk = vm.RequestDiscardHunk,
        };
        var vScrollBar = ScrollBars.CreateVertical(ctx);
        var hScrollBar = ScrollBars.CreateHorizontal(ctx);
        content.Use(() => new ScrollSyncController(content, vScrollBar, hScrollBar));

        // Every non-conflict render is pushed into the persistent content view; a Conflict state
        // swaps in the resolution header instead (see the Switch below), so it's skipped here.
        // Anchored on the content view so the subscription releases on unmount.
        content.Bind(vm.RenderState, state =>
        {
            if (state is not DiffRenderState.Conflict)
                content.SetRenderState(state);
        });

        var diffBody = new BorderLayout
        {
            Center = new Raw { View = content },
            East = new Raw { View = vScrollBar },
            South = new Raw { View = hScrollBar },
        };

        return new Box
        {
            Background = Theme.Color(s => s.DiffView.PanelBackground),
            Children =
            [
                new Switch<bool>
                {
                    // Conflict is the only state that escapes the diff body. Keep both branches
                    // alive so swapping back to the diff preserves its scroll position.
                    Value = new Derived<bool>(() => vm.RenderState.Value is DiffRenderState.Conflict),
                    KeepAlive = true,
                    Case = conflict => conflict ? new ConflictResolveView() : diffBody,
                },
            ],
        };
    }
}
