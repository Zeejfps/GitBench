using ZGF.Gui;
using ZGF.Observable;

namespace GitBench;

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

    // Native title-bar theming (Windows/macOS). Resolved on bind; null on platforms without a
    // window-chrome implementation (e.g. Linux), in which case title-bar theming is skipped.
    private IWindowChrome? _windowChrome;
    private State<ThemeMode>? _themeMode;
    private IDisposable? _themeSubscription;

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
        _themeSubscription?.Dispose();
        foreach (var win in _windows.Values) win.Close();
        _windows.Clear();

        _vm = vm;
        _listSubscription = vm.Windows.Subscribe(OnWindowsChanged);

        // Match every open window's native title bar to the active theme, like the main window
        // (see Program.cs). Subscribe fires immediately, then on each toggle, re-theming all
        // open windows.
        _windowChrome = Context?.Get<IWindowChrome>();
        _themeMode = Context?.Get<State<ThemeMode>>();
        if (_windowChrome != null && _themeMode != null)
            _themeSubscription = _themeMode.Subscribe(_ =>
            {
                foreach (var win in _windows.Values) ApplyTitleBarTheme(win);
            });
    }

    private void ApplyTitleBarTheme(ISecondaryWindow win)
    {
        if (_windowChrome == null || _themeMode == null) return;
        _windowChrome.SetTitleBarTheme(win.Window, _themeMode.Value == ThemeMode.Dark);
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
        ApplyTitleBarTheme(win);
    }

    private void CloseOsWindow(DiffWindowViewModel windowVm)
    {
        if (_windows.Remove(windowVm, out var win))
            win.Close();
    }
}
