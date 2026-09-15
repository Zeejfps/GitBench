using GitBench.App;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Review;

internal sealed record ReviewWindowsView : Widget
{
    protected override IWidget Build(Context ctx)
    {
        var vm = ctx.Require<ReviewWindowsViewModel>();
        var preferences = ctx.Require<PreferencesService>();
        return new SecondaryWindowsHost<ReviewWindowViewModel>
        {
            Windows = vm.Windows,
            Close = vm.Close,
            Request = w => new SecondaryWindowRequest
            {
                BuildRoot = c => Direction.Wrap(new ReviewWindowRootView { Model = w }).BuildView(c),
                Title = w.Title,
                Width = preferences.Current.ReviewWindowWidth,
                Height = preferences.Current.ReviewWindowHeight,
                X = preferences.Current.ReviewWindowX,
                Y = preferences.Current.ReviewWindowY,
            },
            Opened = win =>
            {
                win.Window.OnResize += (w, h) => preferences.Update(p => p with { ReviewWindowWidth = w, ReviewWindowHeight = h });
                win.Window.OnMove += (x, y) => preferences.Update(p => p with { ReviewWindowX = x, ReviewWindowY = y });
            },
            FocusRequests = focus =>
            {
                vm.FocusRequested += focus;
                return new ActionDisposable(() => vm.FocusRequested -= focus);
            },
        };
    }
}
