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
    private readonly TextInputView _nameInput;
    private readonly TextInputView _urlInput;
    private readonly CheckoutDialogKbmController _nameController;
    private readonly SchemeDropdown _schemeDropdown;
    private readonly DialogButton _saveButton;
    private readonly DialogButton _cancelButton;
    private readonly TextView _errorView;
    private readonly Action _onClose;

    private bool _suppressUrlChanged;

    public EditRemoteDialog(Repo repo, string remoteName, Action onClose)
    {
        Width = 540f;
        _onClose = onClose;

        var subtitle = new TextView { Text = "Edit URL of the remote repository" };
        subtitle.BindThemedTextColor(s => s.DialogBody.BodyText);

        var nameLabel = DialogFrame.Label("Remote");
        _nameInput = DialogFrame.TextInput();
        _nameInput.Enter(remoteName);
        var nameBox = DialogFrame.WrapInput(_nameInput);

        var urlLabel = DialogFrame.Label("Repository URL");
        _urlInput = DialogFrame.TextInput();
        _schemeDropdown = new SchemeDropdown();
        var urlRow = new FlexRowView
        {
            Gap = 8,
            CrossAxisAlignment = CrossAxisAlignment.Stretch,
            Height = 28,
            Children =
            {
                new FlexItem { Grow = 1, Child = DialogFrame.WrapInput(_urlInput) },
                _schemeDropdown,
            },
        };

        _errorView = DialogFrame.ErrorView();

        _cancelButton = new DialogButton("Cancel", onClose) { Height = DialogFrame.DefaultButtonHeight };
        _saveButton = new DialogButton("Save") { Height = DialogFrame.DefaultButtonHeight };

        AddChildToSelf(DialogFrame.Build("Remote", onClose, new FlexColumnView
        {
            Gap = 10,
            CrossAxisAlignment = CrossAxisAlignment.Stretch,
            Children =
            {
                subtitle,
                nameLabel,
                nameBox,
                urlLabel,
                urlRow,
                _errorView,
                new MultiChildView { Height = 4 },
                DialogFrame.ButtonsRow(_cancelButton, _saveButton),
            },
        }));

        // Controllers go on the inputs (not the dialog) for the same reason as
        // CreateBranchDialog: an input controller consumes left-press inside its view, so
        // attaching to the outer dialog would swallow clicks meant for the buttons.
        _nameController = new CheckoutDialogKbmController(_nameInput, Confirm, onClose);
        _nameInput.UseController(_ => _nameController);
        _urlInput.UseController(_ => new CheckoutDialogKbmController(_urlInput, Confirm, onClose));

        var request = new EditRemoteRequest(repo, remoteName);
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
        _nameInput.TextChanged += () => vm.SetName(new string(_nameInput.Text));
        _urlInput.TextChanged += () =>
        {
            if (_suppressUrlChanged) return;
            vm.SetUrl(new string(_urlInput.Text));
        };

        vm.UrlReplaced += ReplaceUrlText;
        vm.Scheme.Subscribe(_schemeDropdown.SetScheme);
        _schemeDropdown.SchemeSelected += vm.SetScheme;

        _saveButton.BindBusyCommand(vm.Save);
        _cancelButton.DisableWhile(vm.Save.IsRunning);
        _errorView.BindText(vm.Save.Error, s => s ?? string.Empty);
        vm.CloseRequested += _onClose;

        _nameController.BeginEditing();
    }

    private void ReplaceUrlText(string url)
    {
        if (new string(_urlInput.Text) == url) return;
        _suppressUrlChanged = true;
        _urlInput.Clear();
        _urlInput.Enter(url);
        _suppressUrlChanged = false;
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
