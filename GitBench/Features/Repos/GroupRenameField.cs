using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Components.TextInput;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Repos;

internal sealed record GroupRenameField : Widget
{
    public required Group Group { get; init; }

    protected override View CreateView(Context ctx)
    {
        var registry = ctx.Require<IRepoRegistry>();
        var theme = ctx.Theme();
        var inputSystem = ctx.Require<InputSystem>();

        var input = new TextInputView(ctx.Canvas);
        input.BindThemed(theme, s =>
        {
            input.BackgroundColor = s.GroupRenameField.Background;
            input.TextColor = s.GroupRenameField.Text;
            input.CaretColor = s.GroupRenameField.Caret;
            input.SelectionRectColor = s.GroupRenameField.Selection;
        });
        input.Enter(Group.Name.Value);
        input.SelectAll();

        var box = new RectView
        {
            BorderSize = BorderSizeStyle.All(1),
            BorderRadius = BorderRadiusStyle.All(Radius.Sm),
            Children =
            {
                new PaddingView
                {
                    Padding = new PaddingStyle { Left = Spacing.Xs, Right = Spacing.Xs },
                    Children = { input },
                },
            },
        };
        box.BindBackgroundColor(() => theme.Styles.Value.GroupRenameField.Background);
        box.BindBorderColor(() => BorderColorStyle.All(theme.Styles.Value.GroupRenameField.Border));

        var root = new ContainerView { Height = Sizes.RowHeight };
        root.Children.Add(box);

        root.UseController(inputSystem, () => new GroupRenameKbmController(input, inputSystem, ctx.Get<IClipboard>(), Group.Id, registry));
        return root;
    }
}
