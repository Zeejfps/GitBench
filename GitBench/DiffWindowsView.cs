using ZGF.Gui;
using ZGF.Observable;

namespace GitGui;

/// <summary>
/// Headless host view that reflects <see cref="DiffWindowsViewModel.Windows"/> into real,
/// decorated OS windows — the top-level-window analogue of <c>BindChildren</c>. It draws
/// nothing itself (zero-sized); it exists so the diff-windows view model is created and bound
/// through the standard <c>UseViewModel</c> flow. On Added it opens a window hosting a
/// <see cref="DiffView"/> bound to the entry's <see cref="DiffViewModel"/>; on Removed/Cleared
/// it tears the window down. Native title-bar closes route back through the view model so its
/// observable list stays the single source of truth.
/// </summary>
internal sealed class DiffWindowsView : MultiChildView, IBind<DiffWindowsViewModel>
{
    private readonly Dictionary<DiffWindowViewModel, ISecondaryWindow> _windows = new();
    private DiffWindowsViewModel? _vm;
    private IDisposable? _listSubscription;

    public DiffWindowsView()
    {
        // Logic-only view: it never paints or takes input, so pin it to zero size.
        Width = 0;
        Height = 0;
        this.UseViewModel<DiffWindowsViewModel>(this);
    }

    public void Bind(DiffWindowsViewModel vm)
    {
        // Re-bind safety: drop any previous wiring before adopting the new VM.
        _listSubscription?.Dispose();
        foreach (var win in _windows.Values) win.Close();
        _windows.Clear();

        _vm = vm;
        _listSubscription = vm.Windows.Subscribe(OnWindowsChanged);
    }

    private void OnWindowsChanged(ListChange<DiffWindowViewModel> change)
    {
        switch (change.Kind)
        {
            case ListChangeKind.Added:
                Open(change.Item!);
                break;
            case ListChangeKind.Removed:
                CloseOsWindow(change.OldItem!);
                break;
            case ListChangeKind.Cleared:
            case ListChangeKind.Reset:
                foreach (var win in _windows.Values) win.Close();
                _windows.Clear();
                break;
        }
    }

    private void Open(DiffWindowViewModel windowVm)
    {
        if (_windows.ContainsKey(windowVm)) return;

        var root = new DiffWindowRootView(windowVm);

        var win = Context!.Require<ISecondaryWindowFactory>().Open(new SecondaryWindowRequest
        {
            Root = root,
            Title = windowVm.Title,
            Width = 900,
            Height = 700,
        });
        // Native close → drive removal through the view model so its list stays authoritative;
        // that removal calls CloseOsWindow below (idempotent if the window is already gone).
        win.Closed += () => _vm?.Close(windowVm);
        _windows[windowVm] = win;
    }

    private void CloseOsWindow(DiffWindowViewModel windowVm)
    {
        if (_windows.Remove(windowVm, out var win))
            win.Close();
    }
}
