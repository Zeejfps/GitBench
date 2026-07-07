using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Components.TextInput;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Repos;

// Inline editor for a primary repo's display name, swapped in for the row's name label while
// renaming. Shares the group rename field's themed input styling.
internal sealed record RepoRenameField : Widget
{
    public required Guid RepoId { get; init; }
    public required string InitialName { get; init; }
    public required float RowHeight { get; init; }

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

        root.UseController(inputSystem, () => new RepoRenameKbmController(input, inputSystem, ctx.Get<IClipboard>(), RepoId, registry));
        return root;
    }
}
