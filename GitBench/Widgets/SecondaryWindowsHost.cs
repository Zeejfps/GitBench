using GitBench.Theming;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Widgets;

/// <summary>
/// Zero-sized host that reflects an observable list of per-window view models into real,
/// decorated OS windows — the top-level-window analogue of <c>BindChildren</c>. Native title-bar
/// closes route back through <see cref="Close"/> so the list stays the single source of truth.
/// </summary>
internal sealed record SecondaryWindowsHost<TVm> : Widget where TVm : class
{
    public required ObservableList<TVm> Windows { get; init; }
    public required Action<TVm> Close { get; init; }
    public required Func<TVm, SecondaryWindowRequest> Request { get; init; }
    public Action<ISecondaryWindow>? Opened { get; init; }
    public Func<Action<TVm>, IDisposable>? FocusRequests { get; init; }

    protected override View CreateView(Context ctx) => new Core(ctx, this);

    private sealed class Core : ContainerView
    {
        private readonly Dictionary<TVm, ISecondaryWindow> _windows = new();
        private readonly SecondaryWindowsHost<TVm> _host;
        private readonly ISecondaryWindowFactory _windowFactory;
        private readonly IWindowChrome? _windowChrome;
        private readonly State<ThemeMode>? _themeMode;

        public Core(Context ctx, SecondaryWindowsHost<TVm> host)
        {
            Width = 0;
            Height = 0;
            _host = host;
            _windowFactory = ctx.Require<ISecondaryWindowFactory>();
            _windowChrome = ctx.Get<IWindowChrome>();
            _themeMode = ctx.Get<State<ThemeMode>>();

            this.Use(() => host.Windows.Subscribe(OnWindowsChanged));
            if (host.FocusRequests != null)
                this.Use(() => host.FocusRequests(FocusExisting));
            if (_windowChrome != null && _themeMode != null)
                this.Bind(_themeMode, _ =>
                {
                    foreach (var win in _windows.Values) ApplyTitleBarTheme(win);
                });
        }

        private void ApplyTitleBarTheme(ISecondaryWindow win)
        {
            if (_windowChrome == null || _themeMode == null) return;
            _windowChrome.SetTitleBarTheme(win.Window, _themeMode.Value == ThemeMode.Dark);
        }

        private void OnWindowsChanged(ListChange<TVm> change)
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

        private void Open(TVm windowVm)
        {
            if (_windows.ContainsKey(windowVm)) return;
            var win = _windowFactory.Open(_host.Request(windowVm));
            win.Closed += () => _host.Close(windowVm);
            _host.Opened?.Invoke(win);
            _windows[windowVm] = win;
            ApplyTitleBarTheme(win);
        }

        private void CloseOsWindow(TVm windowVm)
        {
            if (_windows.Remove(windowVm, out var win))
                win.Close();
        }

        private void FocusExisting(TVm windowVm)
        {
            if (!_windows.TryGetValue(windowVm, out var win)) return;
            win.Window.Show();
            win.Window.Focus();
        }
    }
}
