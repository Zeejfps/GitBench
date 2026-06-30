using GitBench.App;
using GitBench.Theming;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Review;

/// <summary>
/// Headless host that reflects <see cref="ReviewWindowsViewModel.Windows"/> into real, decorated
/// OS windows — the review-window analogue of <c>DiffWindowsView</c>. It draws nothing itself
/// (zero-sized); it exists so the review-windows view model is created and bound through the
/// standard <c>UseViewModel</c> flow. On Added it opens a window hosting a
/// <see cref="ReviewWindowRootView"/> bound to the entry's <see cref="ReviewWindowViewModel"/>; on
/// Removed/Cleared it tears the window down. Native title-bar closes route back through the view
/// model so its observable list stays the single source of truth.
/// </summary>
internal sealed record ReviewWindowsView : Widget
{
    protected override View CreateView(Context ctx) => new Core(ctx);

    private sealed class Core : ContainerView
    {
        private readonly Dictionary<ReviewWindowViewModel, ISecondaryWindow> _windows = new();
        private readonly ReviewWindowsViewModel _vm;
        private readonly ISecondaryWindowFactory _windowFactory;
        private readonly PreferencesService _preferences;

        // Native title-bar theming (Windows/macOS). Resolved from the build context; null on
        // platforms without a window-chrome implementation (e.g. Linux), in which case title-bar
        // theming is skipped.
        private readonly IWindowChrome? _windowChrome;
        private readonly State<ThemeMode>? _themeMode;

        public Core(Context ctx)
        {
            // Logic-only view: it never paints or takes input, so pin it to zero size.
            Width = 0;
            Height = 0;

            _windowFactory = ctx.Require<ISecondaryWindowFactory>();
            _preferences = ctx.Require<PreferencesService>();
            _windowChrome = ctx.Get<IWindowChrome>();
            _themeMode = ctx.Get<State<ThemeMode>>();

            var vm = ctx.Require<ReviewWindowsViewModel>();
            _vm = vm;
            this.UseViewModel(() => vm, _ => { });
            this.Use(() => vm.Windows.Subscribe(OnWindowsChanged));

            // Focus-existing: a repeat open request for an already-open review raises this instead
            // of adding a window; bring its OS window forward (and restore it if minimized).
            this.Use(() =>
            {
                void OnFocus(ReviewWindowViewModel w) => FocusExisting(w);
                vm.FocusRequested += OnFocus;
                return new ActionDisposable(() => vm.FocusRequested -= OnFocus);
            });

            // Match every open window's native title bar to the active theme, like the main window
            // (see Program.cs). The binding fires immediately, then on each toggle, re-theming all
            // open windows.
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

        private void OnWindowsChanged(ListChange<ReviewWindowViewModel> change)
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

        private void Open(ReviewWindowViewModel windowVm)
        {
            if (_windows.ContainsKey(windowVm)) return;

            var win = _windowFactory.Open(new SecondaryWindowRequest
            {
                BuildRoot = ctx => new ReviewWindowRootView { Model = windowVm }.BuildView(ctx),
                Title = windowVm.Title,
                Width = _preferences.Current.ReviewWindowWidth,
                Height = _preferences.Current.ReviewWindowHeight,
            });
            // Native close → drive removal through the view model so its list stays authoritative;
            // that removal calls CloseOsWindow below (idempotent if the window is already gone).
            win.Closed += () => _vm.Close(windowVm);
            // Persist size like the main window; all review windows share one remembered size.
            win.Window.OnResize += _preferences.SetReviewWindowSize;
            _windows[windowVm] = win;
            ApplyTitleBarTheme(win);
        }

        private void CloseOsWindow(ReviewWindowViewModel windowVm)
        {
            if (_windows.Remove(windowVm, out var win))
                win.Close();
        }

        private void FocusExisting(ReviewWindowViewModel windowVm)
        {
            if (!_windows.TryGetValue(windowVm, out var win)) return;
            win.Window.Show();
            win.Window.Focus();
        }

        private sealed class ActionDisposable : IDisposable
        {
            private readonly Action _dispose;
            public ActionDisposable(Action dispose) => _dispose = dispose;
            public void Dispose() => _dispose();
        }
    }
}
