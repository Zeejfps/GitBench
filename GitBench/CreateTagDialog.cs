using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitBench;

/// <summary>
/// Modal shown when the user picks "Create Tag" on a commit in the history. Mirrors Fork's
/// "Create Tag" dialog: the target commit, a tag name, an optional annotation message, and a
/// "push to all remotes" toggle. A non-empty message yields an annotated tag, otherwise a
/// lightweight one — see <see cref="IGitService.CreateTag"/>.
/// </summary>
internal sealed class CreateTagDialog : MultiChildView, IBind<CreateTagDialogViewModel>
{
    private readonly Action _onClose;
    private readonly LabeledInputField _nameField;
    private readonly GrowingDescriptionField _messageField;
    private readonly CheckboxView _pushCheckbox;
    private readonly DialogShell _shell;

    public CreateTagDialog(Repo repo, string sha, string shortSha, string summary, Action onClose)
    {
        _onClose = onClose;

        var subtitle = new TextView { Text = "Create annotated tag", TextWrap = TextWrap.Wrap };
        subtitle.BindThemedTextColor(s => s.DialogBody.BodyText);

        var targetRow = BuildLabeledRow("Create tag at:", BuildCommitValue(shortSha, summary));

        _nameField = new LabeledInputField("Tag name") { Placeholder = "Enter Tag Name" };

        var messageLabel = DialogFrame.Label("Message");
        _messageField = new GrowingDescriptionField(72f, 200f) { PlaceholderText = "optional" };

        _pushCheckbox = new CheckboxView("Push to all remotes") { Height = 22 };

        _shell = new DialogShell("Create tag", onClose)
        {
            Width = DialogFrame.WidthWide,
            Action = ("Create", DialogButtonRole.Primary),
            Body =
            {
                subtitle,
                targetRow,
                // Each label sits tight against its field (small intra-group gap); the column's
                // larger Gap separates one section from the next so labels read as attached to
                // their inputs rather than floating midway between them.
                _nameField,
                LabeledField(messageLabel, _messageField),
                _pushCheckbox,
            },
        };
        AddChildToSelf(_shell.View);

        // Submit-on-enter / cancel-on-esc lives on the name input, not the dialog — see
        // CreateBranchDialog: the input controller consumes left-press inside its own view,
        // so attaching to the outer dialog would swallow clicks meant for the buttons. The
        // message field keeps its own multi-line controller so Enter inserts a newline there.
        _shell.SubmitFrom(_nameField.Input);

        var request = new CreateTagRequest(repo, sha);
        this.UseViewModel(
            ctx => new CreateTagDialogViewModel(
                request,
                ctx.Require<IGitService>(),
                ctx.Require<IUiDispatcher>(),
                ctx.Require<IMessageBus>()),
            Bind);
    }

    public void Bind(CreateTagDialogViewModel vm)
    {
        vm.CloseRequested += _onClose;

        _nameField.Input.BindTwoWay(vm.Name);
        _nameField.BindStatus(vm.NameStatus);
        _messageField.BindTwoWay(vm.Message, vm.SetMessage);
        _pushCheckbox.IsChecked.BindTwoWay(vm.PushToAllRemotes);
        _shell.BindCommand(vm.Create);

        // Reflect the toggle in the primary button's label, like Fork ("Create and Push").
        vm.PushToAllRemotes.Subscribe(push => _shell.ActionButton.Label = push ? "Create and Push" : "Create");

        _shell.BeginEditing();
    }

    // Label stacked tightly above its input. The 4px intra-group gap keeps the label visually
    // bound to its field; the parent column's wider Gap does the section spacing.
    private static FlexColumnView LabeledField(View label, View field) => new()
    {
        Gap = 4,
        CrossAxisAlignment = CrossAxisAlignment.Stretch,
        Children = { label, field },
    };

    private static FlexRowView BuildLabeledRow(string label, MultiChildView value)
    {
        var labelText = new TextView { Text = label, VerticalTextAlignment = TextAlignment.Center };
        labelText.BindThemedTextColor(s => s.DialogBody.SectionHeaderText);
        var labelColumn = new FlexRowView
        {
            Width = 100,
            MainAxisAlignment = MainAxisAlignment.End,
            CrossAxisAlignment = CrossAxisAlignment.Center,
            Children = { labelText },
        };
        return new FlexRowView
        {
            Gap = 10,
            CrossAxisAlignment = CrossAxisAlignment.Center,
            Height = 30,
            Children =
            {
                labelColumn,
                new FlexItem { Grow = 1, Child = value },
            },
        };
    }

    private static MultiChildView BuildCommitValue(string shortSha, string summary)
    {
        var dot = new TextView
        {
            Text = "●",
            FontSize = 10,
            VerticalTextAlignment = TextAlignment.Center,
            HorizontalTextAlignment = TextAlignment.Center,
            Width = 16,
        };
        dot.BindThemedTextColor(s => s.DialogBody.BodyText);

        var shaLabel = new TextView { Text = shortSha, VerticalTextAlignment = TextAlignment.Center };
        shaLabel.BindThemedTextColor(s => s.DialogFrame.TitleText);

        // Ellipsis (…) on overflow rather than NoWrap-in-a-ClippingView: the clip let the
        // single line run past the dialog's right edge instead of truncating it. Ellipsis
        // measures against the laid-out Grow width and cuts the text with a trailing "…".
        var summaryLabel = new TextView
        {
            Text = summary,
            VerticalTextAlignment = TextAlignment.Center,
            TextOverflow = TextOverflow.Ellipsis,
        };
        summaryLabel.BindThemedTextColor(s => s.DialogBody.BodyText);

        return new FlexRowView
        {
            Gap = 8,
            CrossAxisAlignment = CrossAxisAlignment.Center,
            Children =
            {
                dot,
                shaLabel,
                new FlexItem { Grow = 1, Child = summaryLabel },
            },
        };
    }
}
