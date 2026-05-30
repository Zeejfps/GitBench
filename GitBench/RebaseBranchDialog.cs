using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitGui;

internal sealed class RebaseBranchDialog : MultiChildView, IBind<RebaseBranchDialogViewModel>
{
    private readonly Action _onClose;
    private readonly DialogButton _rebaseButton;
    private readonly DialogButton _cancelButton;
    private readonly CheckboxView _autostashCheckbox;
    private readonly TextView _previewIcon;
    private readonly TextView _previewText;
    private readonly TextView _errorView;
    private RebasePreviewState _previewState = RebasePreviewState.Unknown;
    private BranchPreviewStyles _previewStyles = ThemeStyles.Dark.BranchPreview;

    public RebaseBranchDialog(RebaseBranchRequest request, Action onClose)
    {
        _onClose = onClose;

        var subtitle = new TextView
        {
            Text = "Copy commits from one branch to another",
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
        };
        subtitle.BindThemedTextColor(s => s.DialogBody.RowTextMissing);

        var rebaseRow = BuildLabeledRow("Rebase:", BuildBranchChip(request.SourceBranch));
        var ontoRow = BuildLabeledRow("On:", BuildBranchChip(request.TargetDisplay));

        _autostashCheckbox = new CheckboxView("Stash and reapply local changes")
        {
            Height = 24,
        };
        var autostashRow = BuildLabeledRow("", _autostashCheckbox);

        _previewIcon = new TextView
        {
            FontFamily = LucideIcons.FontFamily,
            FontSize = 14,
            Text = string.Empty,
            VerticalTextAlignment = TextAlignment.Center,
        };
        _previewText = new TextView
        {
            Text = string.Empty,
            VerticalTextAlignment = TextAlignment.Center,
        };
        _previewIcon.BindThemed(s =>
        {
            _previewStyles = s.BranchPreview;
            ApplyPreviewState();
        });
        var previewChip = new FlexRowView
        {
            Gap = 6,
            CrossAxisAlignment = CrossAxisAlignment.Center,
            Children = { _previewIcon, _previewText },
        };

        _errorView = DialogFrame.ErrorView();

        _cancelButton = new DialogButton("Cancel", onClose) { Height = DialogFrame.DefaultButtonHeight, Width = 96 };
        _rebaseButton = new DialogButton("Rebase") { Height = DialogFrame.DefaultButtonHeight, Width = 96 };

        var buttonsRow = new FlexRowView
        {
            Gap = 8,
            CrossAxisAlignment = CrossAxisAlignment.Center,
            Children =
            {
                new FlexItem { Grow = 1, Child = previewChip },
                _cancelButton,
                _rebaseButton,
            },
        };

        AddChildToSelf(DialogFrame.Build("Rebase", onClose, new FlexColumnView
        {
            Gap = 12,
            CrossAxisAlignment = CrossAxisAlignment.Stretch,
            Children =
            {
                subtitle,
                rebaseRow,
                ontoRow,
                autostashRow,
                _errorView,
                new MultiChildView { Height = 4 },
                buttonsRow,
            },
        }));

        this.UseController(_ => new DialogKbmController(_rebaseButton.Command, onClose));

        this.UseViewModel(
            ctx => new RebaseBranchDialogViewModel(
                request,
                ctx.Require<IGitService>(),
                ctx.Require<IUiDispatcher>(),
                ctx.Require<IMessageBus>()),
            Bind);
    }

    public void Bind(RebaseBranchDialogViewModel vm)
    {
        vm.CloseRequested += _onClose;
        _autostashCheckbox.IsChecked.BindTwoWay(vm.Autostash);
        _rebaseButton.BindBusyCommand(vm.Rebase);
        _cancelButton.DisableWhile(vm.Rebase.IsRunning);
        _errorView.BindText(vm.Rebase.Error, s => s ?? string.Empty);
        vm.PreviewState.Subscribe(s =>
        {
            _previewState = s;
            ApplyPreviewState();
        });
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

    private void ApplyPreviewState()
    {
        switch (_previewState)
        {
            case RebasePreviewState.Clean:
                _previewIcon.Text = LucideIcons.CheckSquare;
                _previewIcon.TextColor = _previewStyles.Clean;
                _previewText.Text = "Rebase can be done without conflicts";
                _previewText.TextColor = _previewStyles.Clean;
                break;
            case RebasePreviewState.Conflicts:
                _previewIcon.Text = LucideIcons.CloudOff;
                _previewIcon.TextColor = _previewStyles.Conflict;
                _previewText.Text = "Rebase will produce conflicts";
                _previewText.TextColor = _previewStyles.Conflict;
                break;
            default:
                _previewIcon.Text = string.Empty;
                _previewText.Text = string.Empty;
                break;
        }
    }
}
