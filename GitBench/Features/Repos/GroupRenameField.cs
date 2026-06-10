using ZGF.Gui;
using ZGF.Gui.Desktop;
using ZGF.Gui.Desktop.Components.TextInput;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Views;

namespace GitBench;

public sealed class GroupRenameField : MultiChildView
{
    public GroupRenameField(Group group, IRepoRegistry registry)
    {
        Height = 22;

        var input = new TextInputView();
        input.BindThemed(s =>
        {
            input.BackgroundColor = s.GroupRenameField.Background;
            input.TextColor = s.GroupRenameField.Text;
            input.CaretColor = s.GroupRenameField.Caret;
            input.SelectionRectColor = s.GroupRenameField.Selection;
        });
        input.Enter(group.Name);
        input.SelectAll();

        var box = new RectView
        {
            BorderSize = BorderSizeStyle.All(1),
            BorderRadius = BorderRadiusStyle.All(3),
            Padding = new PaddingStyle { Left = 4, Right = 4 },
            Children = { input }
        };
        box.BindThemedBackgroundColor(s => s.GroupRenameField.Background);
        box.BindThemedBorderColor(s => BorderColorStyle.All(s.GroupRenameField.Border));
        AddChildToSelf(box);

        this.UseController(ctx =>
        {
            var inputSystem = ctx.Require<InputSystem>();
            return new GroupRenameKbmController(input, inputSystem, group.Id, registry);
        });
    }
}