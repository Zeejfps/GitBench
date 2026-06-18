using GitBench.Controls.Dialogs;
using GitBench.Features.Diff;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Messages;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Worktrees;

/// <summary>
/// Confirmation modal for `git worktree remove`. Refuses if the worktree has uncommitted
/// changes or untracked files unless Force is checked (delegates the safety check to git).
/// </summary>
internal sealed record RemoveWorktreeDialog : Widget
{
    // Mirrors the frame width Build() applies, so the path pre-wrap math below stays in sync.
    private const float DialogWidth = DialogFrame.WidthStandard;
    private const float CodeBlockInnerPadding = 8f;

    public required Repo Primary { get; init; }
    public required Repo Worktree { get; init; }
    public required Action OnClose { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var vm = new RemoveWorktreeDialogViewModel(
            new RemoveWorktreeRequest(Primary, Worktree),
            ctx.Require<IGitService>(),
            ctx.Require<IUiDispatcher>(),
            ctx.Require<IMessageBus>());

        // Path strings have no whitespace, so the framework's word-wrap engine can't break
        // them. Pre-wrap by inserting newlines at path-separator boundaries so the displayed
        // block stays inside the dialog's content width.
        var pathTextStyle = new TextStyle
        {
            FontFamily = DiffOptions.MonoFontFamily,
            FontSize = 12f,
            TextWrap = TextWrap.Wrap,
        };
        var available = DialogWidth
                        - 2 * DialogFrame.DefaultPadding
                        - 2 * CodeBlockInnerPadding
                        - 2; // account for the 1px border on each side of the code-block
        var wrappedPath = PathWrap.Wrap(Worktree.Path, pathTextStyle, available, ctx.Canvas);

        var theme = ctx.Theme();
        var pathTextView = new Text
        {
            Value = wrappedPath,
            FontFamily = DiffOptions.MonoFontFamily,
            FontSize = 12f,
            Wrap = TextWrap.Wrap,
            Color = Theme.Color(s => s.DialogBody.BodyText),
        }.BuildView(ctx);

        var pathBox = new RectView
        {
            BorderSize = BorderSizeStyle.All(1),
            BorderRadius = BorderRadiusStyle.All(4),
            Children =
            {
                new PaddingView
                {
                    Padding = new PaddingStyle
                    {
                        Left = (int)CodeBlockInnerPadding,
                        Right = (int)CodeBlockInnerPadding,
                        Top = 6,
                        Bottom = 6,
                    },
                    Children = { pathTextView },
                },
            },
        };
        pathBox.BindBackgroundColor(() => theme.Styles.Value.DialogFrame.InsetBackground);
        pathBox.BindBorderColor(() => BorderColorStyle.All(theme.Styles.Value.DialogFrame.Border));

        return new Dialog
        {
            Title = "Remove worktree",
            OnClose = OnClose,
            ViewModel = vm,
            Action = ("Remove", DialogButtonRole.Destructive),
            Command = vm.Remove,
            ConfirmKeys = true,
            Body =
            [
                new Text
                {
                    Value = $"Remove worktree '{Worktree.DisplayName}'?",
                    Wrap = TextWrap.Wrap,
                    Color = Theme.Color(s => s.DialogBody.BodyText),
                },
                new Raw { View = pathBox },
                new Checkbox
                {
                    Label = "Remove even if dirty",
                    Checked = vm.Force,
                    Height = 22,
                }.WithController<KbmController>(),
                new Text
                {
                    Value = "git refuses if the worktree has uncommitted changes. Check the box to remove anyway.",
                    Wrap = TextWrap.Wrap,
                    Color = Theme.Color(s => s.DialogBody.RowTextMissing),
                },
            ],
        };
    }
}
