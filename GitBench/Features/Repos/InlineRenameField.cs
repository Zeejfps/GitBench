using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Components.TextInput;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Repos;

// Inline editor swapped in for a repo/group row's name label while renaming. Enter, focus loss and
// a click outside commit; Escape cancels.
internal sealed record InlineRenameField : Widget
{
    public required string InitialName { get; init; }
    public required float RowHeight { get; init; }
    public required Action<string> OnCommit { get; init; }
    public required Action OnCancel { get; init; }

    protected override View CreateView(Context ctx)
    {
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
        input.Enter(InitialName);
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

        var root = new ContainerView { Height = RowHeight };
        root.Children.Add(box);

        root.UseController(inputSystem, () => new InlineRenameKbmController(input, inputSystem, ctx.Require<IClipboard>(), OnCommit, OnCancel));
        return root;
    }
}
