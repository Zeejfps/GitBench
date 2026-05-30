using ZGF.Gui;
using ZGF.Observable;

namespace GitGui;

/// <summary>
/// Shell for the main content area. Mounts both mode-specific views (history and local
/// changes) up front and toggles their visibility based on the observed
/// <see cref="MainViewMode"/>. The inactive view stays attached so its presenter / view
/// model keeps listening to bus events (refs / working-tree / commit changes) and stays
/// continuously up to date — switching modes is then just a visibility flip, with no
/// reload flash and no "Loading…" placeholder.
/// </summary>
public sealed class MainContentView : MultiChildView
{
    private readonly HistoryView _history = new();
    private readonly LocalChangesView _localChanges = new();
    private IDisposable? _modeSubscription;

    public MainContentView()
    {
        AddChildToSelf(_history);
        AddChildToSelf(_localChanges);
    }

    protected override void OnAttachedToContext(Context context)
    {
        var mode = context.Get<State<MainViewMode>>();
        if (mode != null)
            _modeSubscription = mode.Subscribe(SetActive);
    }

    protected override void OnDetachedFromContext(Context context)
    {
        _modeSubscription?.Dispose();
        _modeSubscription = null;
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
