using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Views;

namespace GitBench;

/// <summary>
/// Root view hosted inside a pop-out diff window: a <see cref="DiffWindowToolbar"/> (file
/// path + LFS + whole-file Stage/Unstage) above the headerless <see cref="DiffView"/> body.
/// Bound to a <see cref="DiffWindowViewModel"/> via the standard IBind flow.
/// </summary>
internal sealed class DiffWindowRootView : MultiChildView, IBind<DiffWindowViewModel>
{
    private readonly DiffView _diff = new();
    private readonly DiffWindowToolbar _toolbar;

    public DiffWindowRootView(DiffWindowViewModel vm)
    {
        _toolbar = new DiffWindowToolbar(vm.Title);

        var layout = new BorderLayoutView
        {
            North = _toolbar,
            Center = _diff,
        };

        var panel = new RectView { Children = { layout } };
        panel.BindThemedBackgroundColor(s => s.DiffView.PanelBackground);
        AddChildToSelf(panel);

        // The pop-out has no file list to host the "F" full-file toggle, so wire it at the
        // window root instead (the toolbar button is the other entry point).
        this.UseController(_ => new DiffWindowKeyController(this, vm.Diff.ToggleFullFile));

        Bind(vm);
    }

    public void Bind(DiffWindowViewModel vm)
    {
        _toolbar.Bind(vm.Diff);
        _diff.Bind(vm.Diff);
    }
}
