using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitGui;

/// <summary>
/// Modal shown when the user picks "Edit origin…" (or any remote) on a remote section
/// header. Mirrors Fork's "Remote" dialog: an editable remote name + repository URL, with
/// a scheme dropdown that rewrites the URL between SSH and HTTPS. Runs
/// <c>git remote rename</c> (when the name changed) then <c>git remote set-url</c>.
/// </summary>
internal sealed class EditRemoteDialog : MultiChildView, IBind<EditRemoteDialogViewModel>
{
    private readonly LabeledInputField _nameField;
    private readonly LabeledInputField _urlField;
    private readonly CheckoutDialogKbmController _nameController;
    private readonly SchemeDropdown _schemeDropdown;
    private readonly DialogButton _saveButton;
    private readonly DialogButton _cancelButton;
    private readonly TextView _errorView;
    private readonly Action _onClose;

    public EditRemoteDialog(Repo repo, string remoteName, Action onClose)
        : this(new EditRemoteRequest(repo, remoteName),
               "Remote", "Edit URL of the remote repository", "Save", onClose)
    {
    }

    // Add-mode: seeds the remote name with the conventional "origin" and runs
    // `git remote add` on save instead of rename/set-url.
    public EditRemoteDialog(Repo repo, Action onClose)
        : this(new EditRemoteRequest(repo, "origin", IsAdd: true),
               "Add Remote", "Add a new remote repository", "Add", onClose)
    {
    }

    private EditRemoteDialog(EditRemoteRequest request, string title, string subtitleText, string confirmLabel, Action onClose)
    {
        _onClose = onClose;

        var subtitle = new TextView { Text = subtitleText };
        subtitle.BindThemedTextColor(s => s.DialogBody.BodyText);

        _nameField = new LabeledInputField("Remote");

        _schemeDropdown = new SchemeDropdown();
        _urlField = new LabeledInputField("Repository URL")
        {
            Accessory = _schemeDropdown,
        };

        _errorView = DialogFrame.ErrorView();

        _cancelButton = new DialogButton("Cancel", onClose) { Height = DialogFrame.DefaultButtonHeight };
        _saveButton = new DialogButton(confirmLabel, role: DialogButtonRole.Primary) { Height = DialogFrame.DefaultButtonHeight };

        AddChildToSelf(DialogFrame.Build(title, onClose, new FlexColumnView
        {
            Gap = 10,
            CrossAxisAlignment = CrossAxisAlignment.Stretch,
            Children =
            {
                subtitle,
                _nameField,
                _urlField,
                _errorView,
                new MultiChildView { Height = 4 },
                DialogFrame.ButtonsRow(_cancelButton, _saveButton),
            },
        }, DialogFrame.WidthWide));

        // Controllers go on the inputs (not the dialog) for the same reason as
        // CreateBranchDialog: an input controller consumes left-press inside its view, so
        // attaching to the outer dialog would swallow clicks meant for the buttons.
        _nameController = new CheckoutDialogKbmController(_nameField.Input, Confirm, onClose);
        _nameField.Input.UseController(_ => _nameController);
        _urlField.Input.UseController(_ => new CheckoutDialogKbmController(_urlField.Input, Confirm, onClose));

        this.UseViewModel(
            ctx => new EditRemoteDialogViewModel(
                request,
                ctx.Require<IGitService>(),
                ctx.Require<IUiDispatcher>(),
                ctx.Require<IMessageBus>()),
            Bind);
    }

    public void Bind(EditRemoteDialogViewModel vm)
    {
        _nameField.Input.BindTwoWay(vm.Name);
        _urlField.Input.BindTwoWay(vm.Url);

        vm.Scheme.Subscribe(_schemeDropdown.SetScheme);
        _schemeDropdown.SchemeSelected += vm.SetScheme;

        _saveButton.BindBusyCommand(vm.Save);
        _cancelButton.DisableWhile(vm.Save.IsRunning);
        _errorView.BindText(vm.Save.Error, s => s ?? string.Empty);
        vm.CloseRequested += _onClose;

        _nameController.BeginEditing();
    }

    private void Confirm() => _saveButton.Command.Value?.Execute();
}

/// <summary>
/// Compact SSH/HTTPS toggle shown to the right of the repository URL input. Mirrors
/// <see cref="RemoteDropdown"/>: a bordered button that pops a <see cref="RepoBarContextMenu"/>
/// with the two scheme choices and raises <see cref="SchemeSelected"/> on pick.
/// </summary>
internal sealed class SchemeDropdown : HoverableButton
{
    private readonly TextView _labelView;
    private RemoteUrlScheme _scheme = RemoteUrlScheme.Other;

    public event Action<RemoteUrlScheme>? SchemeSelected;

    public SchemeDropdown()
    {
        Width = 84;
        Height = 28;

        _labelView = new TextView
        {
            Text = LabelFor(_scheme),
            VerticalTextAlignment = TextAlignment.Center,
        };
        _labelView.BindThemedTextColor(s => s.DialogFrame.TitleText);

        var chevron = new TextView
        {
            Text = LucideIcons.ChevronDown,
            FontFamily = LucideIcons.FontFamily,
            FontSize = 12,
            VerticalTextAlignment = TextAlignment.Center,
            HorizontalTextAlignment = TextAlignment.Center,
            Width = 14,
        };
        chevron.BindThemedTextColor(s => s.DialogBody.RowText);

        var row = new FlexRowView
        {
            Gap = 4,
            CrossAxisAlignment = CrossAxisAlignment.Center,
            Children =
            {
                new FlexItem { Grow = 1, Child = _labelView },
                chevron,
            },
        };

        var background = new RectView
        {
            BorderSize = BorderSizeStyle.All(1),
            BorderRadius = BorderRadiusStyle.All(3),
            Padding = new PaddingStyle { Left = 8, Right = 6, Top = 4, Bottom = 4 },
            Children = { row },
        };
        BorderedButtonChrome.Bind(background, IsHovered);
        SetBackground(background);
    }

    public void SetScheme(RemoteUrlScheme scheme)
    {
        _scheme = scheme;
        _labelView.Text = LabelFor(scheme);
    }

    protected override void OnClicked()
    {
        var ctx = Context;
        if (ctx == null) return;
        var items = new List<RepoBarContextMenu.Item>
        {
            new(LabelFor(RemoteUrlScheme.Https), () => SchemeSelected?.Invoke(RemoteUrlScheme.Https)),
            new(LabelFor(RemoteUrlScheme.Ssh), () => SchemeSelected?.Invoke(RemoteUrlScheme.Ssh)),
        };
        RepoBarContextMenu.Show(ctx, Position.BottomLeft, items);
    }

    private static string LabelFor(RemoteUrlScheme scheme) => scheme switch
    {
        RemoteUrlScheme.Https => "HTTPS",
        RemoteUrlScheme.Ssh => "SSH",
        _ => "URL",
    };
}
