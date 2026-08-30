using GitBench.Controls;
using GitBench.Features.LocalChanges;
using GitBench.Git;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Diff;

// Which body the diff pane shows. Most render states draw as a patch; a conflicted file and an
// image blob each take over the pane with their own view.
internal enum DiffBodyKind { Diff, Conflict, Image, Markdown }

/// <summary>
/// The diff body itself: a virtualized, scrollable view of a <see cref="DiffResult"/> with
/// inline per-hunk Stage/Unstage/Discard. It is intentionally headerless — chrome lives in
/// the surrounding context: <see cref="DiffPaneHeaderWidget"/> for the embedded panes (Local
/// Changes, Commit Details) and <see cref="DiffWindowToolbar"/> for the pop-out window.
///
/// When the selected file is a conflicted (unmerged) working-tree file, the body swaps from
/// the diff to a <see cref="ConflictResolveView"/> resolution header; when it is an image
/// blob, to an <see cref="ImagePreviewView"/>.
/// </summary>
internal sealed record DiffView : Widget
{
    /// <summary>Whether a selection here offers the assistant's quick actions. Set by the main
    /// window's diff pane only — see <see cref="DiffContentView.AssistantActions"/>.</summary>
    public bool AssistantActions { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var vm = ctx.Require<DiffViewModel>();

        var content = new DiffContentView(ctx)
        {
            AssistantActions = AssistantActions,
            OnStageHunk = vm.StageHunk,
            OnUnstageHunk = vm.UnstageHunk,
            OnDiscardHunk = vm.RequestDiscardHunk,
            OnExpandGap = vm.ExpandGap,
        };
        var vScrollBar = ScrollBars.CreateVertical(ctx);
        var hScrollBar = ScrollBars.CreateHorizontal(ctx);
        // The code grid it scrolls is pinned LTR (see DiffRowPainter), so the bar must not mirror:
        // normalized 0 stays the left edge in every locale.
        hScrollBar.IsRtl = false;
        content.Use(() => new ScrollSyncController(content, vScrollBar, hScrollBar));

        // Every render that the diff body actually draws is pushed into the persistent content
        // view; Conflict and Image swap in their own body instead (see the Switch below), so
        // they're skipped here. Anchored on the content view so the subscription releases on unmount.
        content.Bind(vm.RenderState, state =>
        {
            if (state is not (DiffRenderState.Conflict or DiffRenderState.Image or DiffRenderState.Markdown))
                content.SetRenderState(state);
        });
        content.Bind(vm.WorkingTreeHunkStates, content.SetWorkingTreeHunkStates);

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
                new Switch<DiffBodyKind>
                {
                    // Conflict and Image are the states that escape the diff body. Keep every
                    // branch alive so swapping back to the diff preserves its scroll position.
                    Value = new Derived<DiffBodyKind>(() => vm.RenderState.Value switch
                    {
                        DiffRenderState.Conflict => DiffBodyKind.Conflict,
                        DiffRenderState.Image => DiffBodyKind.Image,
                        DiffRenderState.Markdown => DiffBodyKind.Markdown,
                        _ => DiffBodyKind.Diff,
                    }),
                    KeepAlive = true,
                    Case = kind => kind switch
                    {
                        DiffBodyKind.Conflict => new ConflictResolveView(),
                        DiffBodyKind.Image => new ImagePreviewView(),
                        DiffBodyKind.Markdown => new MarkdownPreviewView(),
                        _ => diffBody,
                    },
                },
            ],
        };
    }
}
