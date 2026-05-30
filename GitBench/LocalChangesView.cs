using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;

namespace GitGui;

internal sealed class LocalChangesView : MultiChildView, IBind<LocalChangesViewModel>
{
    private readonly LocalChangesContentView _content;
    private readonly CommitBarView _commitBar;

    public LocalChangesView()
    {
        _content = new LocalChangesContentView();
        _commitBar = new CommitBarView();

        var bg = new RectView
        {
            Children =
            {
                new BorderLayoutView
                {
                    Center = _content,
                    South = _commitBar,
                },
            },
        };
        bg.BindThemedBackgroundColor(s => s.Palette.Surface);
        AddChildToSelf(bg);

        // Tab cycles unstaged files → commit title → commit description → commit button
        // (when enabled) → (back to files).
        var focusRing = new FocusRing();
        _content.RegisterFocusStops(focusRing);
        _commitBar.RegisterFocusStops(focusRing);
        _commitBar.RegisterCommitButtonStop(focusRing);

        this.UseViewModel(this);
    }

    public void Bind(LocalChangesViewModel vm)
    {
        _content.Bind(vm);
        _commitBar.Bind(vm);
    }
}
