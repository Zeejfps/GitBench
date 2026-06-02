using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitGui;

/// <summary>
/// The diff body itself: a virtualized, scrollable view of a <see cref="DiffResult"/> with
/// inline per-hunk Stage/Unstage/Discard. It is intentionally headerless — chrome lives in
/// the surrounding context: <see cref="DiffPaneHeader"/> for the embedded panes (Local
/// Changes, Commit Details) and <see cref="DiffWindowToolbar"/> for the pop-out window.
/// </summary>
/// <remarks>
/// Rendering is virtualized — only rows intersecting the viewport are drawn (see
/// <see cref="DiffContentView"/>).
/// </remarks>
internal sealed class DiffView : MultiChildView, IBind<DiffViewModel>
{
    private readonly DiffContentView _content;

    public DiffView()
    {
        _content = new DiffContentView();
        var vScrollBar = ScrollBars.CreateVertical();
        var hScrollBar = ScrollBars.CreateHorizontal();

        var body = new BorderLayoutView
        {
            Center = _content,
            East = vScrollBar,
            South = hScrollBar,
        };

        var panel = new RectView { Children = { body } };
        panel.BindThemedBackgroundColor(s => s.DiffView.PanelBackground);
        AddChildToSelf(panel);

        this.UseBehavior(_ => new ScrollSyncController(_content, vScrollBar, hScrollBar));
    }

    public void Bind(DiffViewModel vm)
    {
        vm.RenderState.Subscribe(_content.SetRenderState);
        _content.OnStageHunk = vm.StageHunk;
        _content.OnUnstageHunk = vm.UnstageHunk;
        _content.OnDiscardHunk = vm.RequestDiscardHunk;
    }
}
