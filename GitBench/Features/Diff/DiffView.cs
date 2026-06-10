using ZGF.Gui;
using ZGF.Gui.Views;

namespace GitBench;

/// <summary>
/// The diff body itself: a virtualized, scrollable view of a <see cref="DiffResult"/> with
/// inline per-hunk Stage/Unstage/Discard. It is intentionally headerless — chrome lives in
/// the surrounding context: <see cref="DiffPaneHeader"/> for the embedded panes (Local
/// Changes, Commit Details) and <see cref="DiffWindowToolbar"/> for the pop-out window.
///
/// When the selected file is a conflicted (unmerged) working-tree file, the body swaps from
/// the diff to a <see cref="ConflictResolveView"/> resolution header.
/// </summary>
/// <remarks>
/// Rendering is virtualized — only rows intersecting the viewport are drawn (see
/// <see cref="DiffContentView"/>).
/// </remarks>
internal sealed class DiffView : MultiChildView, IBind<DiffViewModel>
{
    private readonly DiffContentView _content;
    private readonly RectView _panel;
    private readonly View _diffBody;

    private DiffViewModel? _vm;
    private ConflictResolveView? _conflictView;
    private View? _panelChild;

    public DiffView()
    {
        _content = new DiffContentView();
        var vScrollBar = ScrollBars.CreateVertical();
        var hScrollBar = ScrollBars.CreateHorizontal();

        _diffBody = new BorderLayoutView
        {
            Center = _content,
            East = vScrollBar,
            South = hScrollBar,
        };

        _panel = new RectView { Children = { _diffBody } };
        _panelChild = _diffBody;
        _panel.BindThemedBackgroundColor(s => s.DiffView.PanelBackground);
        AddChildToSelf(_panel);

        this.Use(_ => new ScrollSyncController(_content, vScrollBar, hScrollBar));
    }

    public void Bind(DiffViewModel vm)
    {
        _vm = vm;
        vm.RenderState.Subscribe(OnRenderState);
        _content.OnStageHunk = vm.StageHunk;
        _content.OnUnstageHunk = vm.UnstageHunk;
        _content.OnDiscardHunk = vm.RequestDiscardHunk;
    }

    private void OnRenderState(DiffRenderState state)
    {
        if (state is DiffRenderState.Conflict conflict)
        {
            ShowConflict(conflict);
            return;
        }
        SetPanelChild(_diffBody);
        _content.SetRenderState(state);
    }

    private void ShowConflict(DiffRenderState.Conflict conflict)
    {
        _conflictView ??= new ConflictResolveView(
            onTakeOurs: () => _vm?.ResolveTakeOurs(),
            onTakeTheirs: () => _vm?.ResolveTakeTheirs(),
            onTakeBoth: () => _vm?.ResolveTakeBoth(),
            onOpenInEditor: () => _vm?.OpenConflictInEditor(),
            onMarkResolved: () => _vm?.ResolveMarkResolved());
        _conflictView.SetContext(conflict.Path, conflict.Context);
        SetPanelChild(_conflictView);
    }

    private void SetPanelChild(View child)
    {
        if (ReferenceEquals(_panelChild, child)) return;
        _panel.Children.Clear();
        _panel.Children.Add(child);
        _panelChild = child;
    }
}
