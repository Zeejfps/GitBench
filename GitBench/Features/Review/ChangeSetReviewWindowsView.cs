using GitBench.App;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Review;

/// <summary>
/// Headless host that reflects <see cref="ChangeSetReviewWindowsViewModel.Windows"/> into real,
/// decorated OS windows — the change-set analogue of <see cref="ReviewWindowsView"/>. Draws nothing
/// (zero-sized); on Added it opens a window hosting a <see cref="ChangeSetReviewRootView"/> bound to
/// the entry's <see cref="ChangeSetReviewViewModel"/>; on Removed/Cleared it tears the window down.
/// Native title-bar closes route back through the view model so its observable list stays authoritative.
/// </summary>
internal sealed record ChangeSetReviewWindowsView : Widget
{
    protected override View CreateView(Context ctx) => new Core(ctx);

    private sealed class Core : ContainerView
    {
        private readonly Dictionary<ChangeSetReviewViewModel, ISecondaryWindow> _windows = new();
        private readonly ChangeSetReviewWindowsViewModel _vm;
        private readonly ISecondaryWindowFactory _windowFactory;
        private readonly PreferencesService _preferences;

        private readonly IWindowChrome? _windowChrome;
        private readonly State<ThemeMode>? _themeMode;

        public Core(Context ctx)
        {
            Width = 0;
            Height = 0;

            _windowFactory = ctx.Require<ISecondaryWindowFactory>();
            _preferences = ctx.Require<PreferencesService>();
            _windowChrome = ctx.Get<IWindowChrome>();
            _themeMode = ctx.Get<State<ThemeMode>>();

            var vm = ctx.Require<ChangeSetReviewWindowsViewModel>();
            _vm = vm;
            this.UseViewModel(() => vm, _ => { });
            this.Use(() => vm.Windows.Subscribe(OnWindowsChanged));

            this.Use(() =>
            {
                void OnFocus(ChangeSetReviewViewModel w) => FocusExisting(w);
                vm.FocusRequested += OnFocus;
                return new ActionDisposable(() => vm.FocusRequested -= OnFocus);
            });

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

        private void OnWindowsChanged(ListChange<ChangeSetReviewViewModel> change)
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

        private void Open(ChangeSetReviewViewModel windowVm)
        {
            if (_windows.ContainsKey(windowVm)) return;

            var win = _windowFactory.Open(new SecondaryWindowRequest
            {
                BuildRoot = ctx => Direction.Wrap(new ChangeSetReviewRootView { Model = windowVm }).BuildView(ctx),
                Title = windowVm.Title,
                Width = _preferences.Current.ReviewWindowWidth,
                Height = _preferences.Current.ReviewWindowHeight,
            });
            win.Closed += () => _vm.Close(windowVm);
            win.Window.OnResize += _preferences.SetReviewWindowSize;
            _windows[windowVm] = win;
            ApplyTitleBarTheme(win);
        }

        private void CloseOsWindow(ChangeSetReviewViewModel windowVm)
        {
            if (_windows.Remove(windowVm, out var win))
                win.Close();
        }

        private void FocusExisting(ChangeSetReviewViewModel windowVm)
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
