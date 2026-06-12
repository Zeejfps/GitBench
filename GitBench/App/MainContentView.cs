using ZGF.Gui.Views;
using GitBench.Features.Commits;
using GitBench.Features.LocalChanges;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Observable;

namespace GitBench.App;

/// <summary>
/// Shell for the main content area. Mounts both mode-specific views (history and local
/// changes) up front and toggles their visibility based on the observed
/// <see cref="MainViewMode"/>. The inactive view stays attached so its presenter / view
/// model keeps listening to bus events (refs / working-tree / commit changes) and stays
/// continuously up to date — switching modes is then just a visibility flip, with no
/// reload flash and no "Loading…" placeholder.
/// </summary>
public sealed class MainContentView : ContainerView
{
    private readonly HistoryView _history;
    private readonly LocalChangesView _localChanges = new();

    public MainContentView(Context ctx)
    {
        _history = new HistoryView(ctx);
        AddChildToSelf(_history);
        AddChildToSelf(_localChanges);
        this.Bind(ctx.Require<State<MainViewMode>>(), SetActive);
    }

    private void SetActive(MainViewMode mode)
    {
        _history.IsVisible = mode == MainViewMode.History;
        _localChanges.IsVisible = mode == MainViewMode.LocalChanges;
    }

    protected override void OnLayoutChildren()
    {
        var pos = Position;
        LayoutChildToFill(_history, pos);
        LayoutChildToFill(_localChanges, pos);
    }

    private static void LayoutChildToFill(View child, ZGF.Geometry.RectF pos)
    {
        child.LeftConstraint = pos.Left;
        child.BottomConstraint = pos.Bottom;
        child.WidthConstraint = pos.Width;
        child.HeightConstraint = pos.Height;
        child.LayoutSelf();
    }
}
