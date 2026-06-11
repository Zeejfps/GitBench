using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Diff;

/// <summary>
/// Root widget hosted inside a pop-out diff window: a <see cref="DiffWindowToolbar"/> (file
/// path + LFS + whole-file Stage/Unstage) above the headerless <see cref="DiffView"/> body.
/// Bound to a <see cref="DiffWindowViewModel"/> supplied by the opening
/// <see cref="DiffWindowsView"/>.
/// </summary>
internal sealed record DiffWindowRootView : Widget
{
    public required DiffWindowViewModel Model { get; init; }

    protected override View CreateView(Context ctx)
    {
        var vm = Model;

        var diff = new DiffView(ctx);
        var toolbar = new DiffWindowToolbar(ctx) { Title = vm.Title };

        var layout = new BorderLayoutView
        {
            North = toolbar,
            Center = diff,
        };

        var panel = new RectView { Children = { layout } };
        panel.BindThemedBackgroundColor(ctx.Theme(), s => s.DiffView.PanelBackground);

        // The pop-out has no file list to host the "F" full-file toggle, so wire it at the
        // window root instead (the toolbar button is the other entry point).
        panel.UseController(ctx.Require<InputSystem>(),
            () => new DiffWindowKeyController(panel, vm.Diff.ToggleFullFile));

        toolbar.Bind(vm.Diff);
        diff.Bind(vm.Diff);

        return panel;
    }
}
