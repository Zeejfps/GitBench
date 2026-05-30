using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitGui;

internal sealed class PublishBranchDialog : MultiChildView, IBind<PublishBranchDialogViewModel>
{
    private readonly Action _onClose;
    private readonly DialogButton _publishButton;
    private readonly DialogButton _cancelButton;
    private readonly TextView _errorView;
    private readonly CheckboxView _trackCheckbox;
    private readonly RemoteDropdown _remoteDropdown;

    public PublishBranchDialog(PublishBranchRequest request, Action onClose)
    {
        _onClose = onClose;

        var subtitle = new TextView
        {
            Text = "First push — choose a remote and set the upstream",
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
        };
        subtitle.BindThemedTextColor(s => s.DialogBody.RowTextMissing);

        var branchRow = BuildLabeledRow("Branch:", BuildBranchChip(request.LocalBranch));

        _remoteDropdown = new RemoteDropdown();
        var remoteRow = BuildLabeledRow("To:", _remoteDropdown);

        _trackCheckbox = new CheckboxView("Track this remote branch (set upstream)")
        {
            Height = 24,
        };
        var trackRow = BuildLabeledRow("", _trackCheckbox);

        _errorView = DialogFrame.ErrorView();

        _cancelButton = new DialogButton("Cancel", onClose) { Height = DialogFrame.DefaultButtonHeight, Width = 96 };
        _publishButton = new DialogButton("Publish") { Height = DialogFrame.DefaultButtonHeight, Width = 96 };

        var buttonsRow = new FlexRowView
        {
            Gap = 8,
            CrossAxisAlignment = CrossAxisAlignment.Center,
            Children =
            {
                new FlexItem { Grow = 1, Child = new MultiChildView() },
                _cancelButton,
                _publishButton,
            },
        };

        AddChildToSelf(DialogFrame.Build("Publish branch", onClose, new FlexColumnView
        {
            Gap = 12,
            CrossAxisAlignment = CrossAxisAlignment.Stretch,
            Children =
            {
                subtitle,
                branchRow,
                remoteRow,
                trackRow,
                _errorView,
                new MultiChildView { Height = 4 },
                buttonsRow,
            },
        }));

        this.UseController(_ => new DialogKbmController(_publishButton.Command, onClose));

        this.UseViewModel(
            ctx => new PublishBranchDialogViewModel(
                request,
                ctx.Require<IGitService>(),
                ctx.Require<IUiDispatcher>(),
                ctx.Require<IMessageBus>()),
            Bind);
    }

    public void Bind(PublishBranchDialogViewModel vm)
    {
        vm.CloseRequested += _onClose;

        _remoteDropdown.SelectedState.BindTwoWay(vm.SelectedRemote);
        _trackCheckbox.IsChecked.BindTwoWay(vm.SetUpstream);
        _publishButton.BindBusyCommand(vm.Publish);
        _cancelButton.DisableWhile(vm.Publish.IsRunning);
        _errorView.BindText(vm.ErrorMessage, s => s ?? string.Empty);

        vm.Remotes.Subscribe(remotes => _remoteDropdown.SetOptions(remotes));
    }

    private static FlexRowView BuildLabeledRow(string label, MultiChildView value)
    {
        var labelText = new TextView
        {
            Text = label,
            VerticalTextAlignment = TextAlignment.Center,
        };
        labelText.BindThemedTextColor(s => s.DialogBody.SectionHeaderText);
        var labelColumn = new FlexRowView
        {
            Width = 110,
            MainAxisAlignment = MainAxisAlignment.End,
            CrossAxisAlignment = CrossAxisAlignment.Center,
            Children = { labelText },
        };
        return new FlexRowView
        {
            Gap = 10,
            CrossAxisAlignment = CrossAxisAlignment.Center,
            Height = 28,
            Children =
            {
                labelColumn,
                new FlexItem { Grow = 1, Child = value },
            },
        };
    }

    private static FlexRowView BuildBranchChip(string name)
    {
        var icon = new TextView
        {
            Text = LucideIcons.Branch,
            FontFamily = LucideIcons.FontFamily,
            FontSize = 14,
            VerticalTextAlignment = TextAlignment.Center,
        };
        icon.BindThemedTextColor(s => s.DialogBody.BodyText);

        var label = new TextView
        {
            Text = name,
            VerticalTextAlignment = TextAlignment.Center,
        };
        label.BindThemedTextColor(s => s.DialogFrame.TitleText);
        return new FlexRowView
        {
            Gap = 6,
            CrossAxisAlignment = CrossAxisAlignment.Center,
            Children = { icon, label },
        };
    }
}

internal readonly record struct PublishBranchRequest(Repo Repo, string LocalBranch);

internal sealed class RemoteDropdown : HoverableButton
{
    private readonly TextView _labelView;
    private readonly TextView _chevron;
    private IReadOnlyList<string> _options = Array.Empty<string>();

    public State<string> SelectedState { get; } = new(string.Empty);
    public string Selected => SelectedState.Value;

    public RemoteDropdown()
    {
        Height = 30;

        var icon = new TextView
        {
            Text = LucideIcons.Branch,
            FontFamily = LucideIcons.FontFamily,
            FontSize = 14,
            VerticalTextAlignment = TextAlignment.Center,
        };
        icon.BindThemedTextColor(s => s.DialogBody.BodyText);

        _labelView = new TextView
        {
            Text = "(no remotes)",
            VerticalTextAlignment = TextAlignment.Center,
        };
        _labelView.BindThemedTextColor(s => string.IsNullOrEmpty(SelectedState.Value)
            ? s.DialogBody.RowTextMissing
            : s.DialogFrame.TitleText);

        _chevron = new TextView
        {
            Text = LucideIcons.ChevronDown,
            FontFamily = LucideIcons.FontFamily,
            FontSize = 12,
            VerticalTextAlignment = TextAlignment.Center,
            HorizontalTextAlignment = TextAlignment.Center,
            Width = 16,
        };
        _chevron.BindThemedTextColor(s => s.DialogBody.RowText);

        var row = new FlexRowView
        {
            Gap = 6,
            CrossAxisAlignment = CrossAxisAlignment.Center,
            Children =
            {
                icon,
                new FlexItem { Grow = 1, Child = _labelView },
                _chevron,
            },
        };

        var background = new RectView
        {
            BorderSize = BorderSizeStyle.All(1),
            BorderRadius = BorderRadiusStyle.All(3),
            Padding = new PaddingStyle { Left = 8, Right = 8, Top = 4, Bottom = 4 },
            Children = { row },
        };
        BorderedButtonChrome.Bind(background, IsHovered);
        SetBackground(background);

        SelectedState.Subscribe(s =>
        {
            _labelView.Text = string.IsNullOrEmpty(s) ? "(no remotes)" : s;
        });
    }

    public void SetOptions(IReadOnlyList<string> options)
    {
        _options = options;
        if (options.Count == 0)
        {
            IsEnabled.Value = false;
            _chevron.Text = string.Empty;
            return;
        }
        IsEnabled.Value = true;
        _chevron.Text = options.Count > 1 ? LucideIcons.ChevronDown : string.Empty;
    }

    protected override void OnClicked()
    {
        if (_options.Count <= 1) return;
        var ctx = Context;
        if (ctx == null) return;
        var items = new List<RepoBarContextMenu.Item>(_options.Count);
        foreach (var opt in _options)
        {
            var captured = opt;
            items.Add(new RepoBarContextMenu.Item(
                captured,
                () => SelectedState.Value = captured));
        }
        RepoBarContextMenu.Show(ctx, Position.BottomLeft, items);
    }
}
