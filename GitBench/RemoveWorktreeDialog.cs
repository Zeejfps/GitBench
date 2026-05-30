using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitGui;

/// <summary>
/// Confirmation modal for `git worktree remove`. Refuses if the worktree has uncommitted
/// changes or untracked files unless Force is checked (delegates the safety check to git).
/// </summary>
internal sealed class RemoveWorktreeDialog : MultiChildView, IBind<RemoveWorktreeDialogViewModel>
{
    private const float DialogWidth = 460f;
    private const float CodeBlockInnerPadding = 8f;

    private readonly string _path;
    private readonly Action _onClose;
    private readonly CheckboxView _forceCheckbox;
    private readonly DialogButton _removeButton;
    private readonly DialogButton _cancelButton;
    private readonly TextView _errorView;
    private readonly TextView _pathTextView;
    private readonly TextStyle _pathTextStyle;

    public RemoveWorktreeDialog(Repo primary, Repo worktree, Action onClose)
    {
        Width = DialogWidth;
        _onClose = onClose;
        _path = worktree.Path;

        var prompt = new TextView
        {
            Text = $"Remove worktree '{worktree.DisplayName}'?",
            TextWrap = TextWrap.Wrap,
        };
        prompt.BindThemedTextColor(s => s.DialogBody.BodyText);

        _pathTextStyle = new TextStyle
        {
            FontFamily = DiffOptions.MonoFontFamily,
            FontSize = 12f,
            TextWrap = TextWrap.Wrap,
        };
        _pathTextView = new TextView
        {
            Text = worktree.Path,
            FontFamily = DiffOptions.MonoFontFamily,
            FontSize = 12f,
            TextWrap = TextWrap.Wrap,
        };
        _pathTextView.BindThemed(s =>
        {
            _pathTextView.TextColor = s.DialogBody.BodyText;
            _pathTextStyle.TextColor = s.DialogBody.BodyText;
        });

        var pathBox = new RectView
        {
            BorderSize = BorderSizeStyle.All(1),
            BorderRadius = BorderRadiusStyle.All(4),
            Padding = new PaddingStyle
            {
                Left = (int)CodeBlockInnerPadding,
                Right = (int)CodeBlockInnerPadding,
                Top = 6,
                Bottom = 6,
            },
            Children = { _pathTextView },
        };
        pathBox.BindThemedBackgroundColor(s => s.DialogFrame.InsetBackground);
        pathBox.BindThemedBorderColor(s => BorderColorStyle.All(s.DialogFrame.Border));

        var hint = DialogFrame.Hint(
            "git refuses if the worktree has uncommitted changes. Check the box to remove anyway.",
            TextWrap.Wrap);

        _forceCheckbox = new CheckboxView("Remove even if dirty")
        {
            Height = 22,
        };

        _errorView = DialogFrame.ErrorView();

        _cancelButton = new DialogButton("Cancel", onClose) { Height = DialogFrame.DefaultButtonHeight };
        _removeButton = new DialogButton("Remove") { Height = DialogFrame.DefaultButtonHeight };

        var dialogBody = DialogFrame.Build("Remove worktree", onClose, new FlexColumnView
        {
            Gap = 12,
            CrossAxisAlignment = CrossAxisAlignment.Stretch,
            Children =
            {
                prompt,
                pathBox,
                _forceCheckbox,
                hint,
                _errorView,
                new MultiChildView { Height = 4 },
                DialogFrame.ButtonsRow(_cancelButton, _removeButton),
            },
        });

        // ClippingView wraps the dialog so a child that measures too wide (e.g. a path that
        // can't be word-broken because it has no spaces) still can't draw past the dialog's
        // rounded edge. The path block also does its own pre-wrap on attach below.
        var clip = new ClippingView();
        clip.Children.Add(dialogBody);
        AddChildToSelf(clip);

        this.UseController(_ => new DialogKbmController(_removeButton.Command, onClose));

        var request = new RemoveWorktreeRequest(primary, worktree);
        this.UseViewModel(
            ctx => new RemoveWorktreeDialogViewModel(
                request,
                ctx.Require<IGitService>(),
                ctx.Require<IUiDispatcher>(),
                ctx.Require<IMessageBus>()),
            Bind);
    }

    public void Bind(RemoveWorktreeDialogViewModel vm)
    {
        vm.CloseRequested += _onClose;
        _forceCheckbox.IsChecked.BindTwoWay(vm.Force);
        _removeButton.BindBusyCommand(vm.Remove);
        _cancelButton.DisableWhile(vm.Remove.IsRunning);
        _errorView.BindText(vm.Remove.Error, s => s ?? string.Empty);
    }

    protected override void OnAttachedToContext(Context context)
    {
        base.OnAttachedToContext(context);
        // Path strings have no whitespace, so the framework's word-wrap engine can't break
        // them. Pre-wrap by inserting newlines at path-separator boundaries so the displayed
        // block stays inside the dialog's content width.
        var available = DialogWidth
                        - 2 * DialogFrame.DefaultPadding
                        - 2 * CodeBlockInnerPadding
                        - 2; // account for the 1px border on each side of the code-block
        _pathTextView.Text = PathWrap.Wrap(_path, _pathTextStyle, available, context.Canvas);
    }
}
