using ZGF.Gui;

namespace GitGui;

public sealed class GroupSectionController : KeyboardMouseController, IDisposable
{
    private readonly View _view;
    private readonly IDragController? _dragController;

    public GroupSectionController(View view, Context context, Guid groupId)
    {
        _view = view;
        _dragController = context.Get<IDragController>();
        _dragController?.RegisterGroupSection(view, groupId);
    }

    public void Dispose()
    {
        _dragController?.Unregister(_view);
    }
}
