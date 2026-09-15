using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Diff;

internal sealed record DiffWindowsView : Widget
{
    protected override IWidget Build(Context ctx)
    {
        var vm = ctx.Require<DiffWindowsViewModel>();
        return new SecondaryWindowsHost<DiffWindowViewModel>
        {
            Windows = vm.Windows,
            Close = vm.Close,
            Request = w => new SecondaryWindowRequest
            {
                BuildRoot = c => Direction.Wrap(new DiffWindowRootView { Model = w }).BuildView(c),
                Title = w.Title,
                Width = 900,
                Height = 700,
            },
        };
    }
}
