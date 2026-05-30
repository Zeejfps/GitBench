using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitGui;

internal sealed class MergeBranchDialog : MultiChildView, IBind<MergeBranchDialogViewModel>
{
    private readonly Action _onClose;
    private readonly DialogButton _mergeButton;
    private readonly DialogButton _cancelButton;
    private readonly MergeOptionDropdown _optionDropdown;
    private readonly TextView _previewIcon;
    private readonly TextView _previewText;
    private readonly TextView _errorView;
    private MergePreviewState _previewState = MergePreviewState.Unknown;
    private BranchPreviewStyles _previewStyles = ThemeStyles.Dark.BranchPreview;

    public MergeBranchDialog(MergeBranchRequest request, Action onClose)
    {
        _onClose = onClose;

        var mergeRow = BuildLabeledRow("Merge:", BuildBranchChip(request.SourceDisplay));
        var intoRow = BuildLabeledRow("Into:", BuildBranchChip(request.TargetBranch));

        _optionDropdown = new MergeOptionDropdown();
        var optionRow = BuildLabeledRow("Merge Option:", _optionDropdown);

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
        _mergeButton = new DialogButton("Merge") { Height = DialogFrame.DefaultButtonHeight, Width = 96 };

        var buttonsRow = new FlexRowView
        {
            Gap = 8,
            CrossAxisAlignment = CrossAxisAlignment.Center,
            Children =
            {
                new FlexItem { Grow = 1, Child = previewChip },
                _cancelButton,
                _mergeButton,
            },
        };

        AddChildToSelf(DialogFrame.Build("Merge branch", onClose, new FlexColumnView
        {
            Gap = 12,
            CrossAxisAlignment = CrossAxisAlignment.Stretch,
            Children =
            {
                mergeRow,
                intoRow,
                optionRow,
                _errorView,
                new MultiChildView { Height = 4 },
                buttonsRow,
            },
        }));

        this.UseController(_ => new DialogKbmController(_mergeButton.Command, onClose));

        this.UseViewModel(
            ctx => new MergeBranchDialogViewModel(
                request,
                ctx.Require<IGitService>(),
                ctx.Require<IUiDispatcher>(),
                ctx.Require<IMessageBus>()),
            Bind);
    }

    public void Bind(MergeBranchDialogViewModel vm)
    {
        vm.CloseRequested += _onClose;
        _optionDropdown.SelectedState.BindTwoWay(vm.Strategy);
        _mergeButton.BindBusyCommand(vm.Merge);
        _cancelButton.DisableWhile(vm.Merge.IsRunning);
        _errorView.BindText(vm.Merge.Error, s => s ?? string.Empty);
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
            case MergePreviewState.Clean:
                _previewIcon.Text = LucideIcons.CheckSquare;
                _previewIcon.TextColor = _previewStyles.Clean;
                _previewText.Text = "Merge can be done without conflicts";
                _previewText.TextColor = _previewStyles.Clean;
                break;
            case MergePreviewState.Conflicts:
                _previewIcon.Text = LucideIcons.CloudOff;
                _previewIcon.TextColor = _previewStyles.Conflict;
                _previewText.Text = "Merge will produce conflicts";
                _previewText.TextColor = _previewStyles.Conflict;
                break;
            default:
                _previewIcon.Text = string.Empty;
                _previewText.Text = string.Empty;
                break;
        }
    }
}

internal sealed class MergeOptionDropdown : HoverableButton
{
    private static readonly (MergeStrategy Strategy, string Label, string Detail)[] Options =
    {
        (MergeStrategy.Default, "Default", "Fast-forward if possible"),
        (MergeStrategy.NoFastForward, "Create merge commit", "Always create a merge commit"),
        (MergeStrategy.FastForwardOnly, "Fast-forward only", "Fail if not fast-forward"),
        (MergeStrategy.Squash, "Squash", "Stage changes for a new commit"),
    };

    private readonly TextView _labelView;
    private readonly TextView _detailView;
    public State<MergeStrategy> SelectedState { get; } = new(MergeStrategy.Default);

    public MergeStrategy Selected => SelectedState.Value;

    public MergeOptionDropdown()
    {
        Height = 30;
        _labelView = new TextView
        {
            Text = LookupLabel(MergeStrategy.Default),
            VerticalTextAlignment = TextAlignment.Center,
        };
        _labelView.BindThemedTextColor(s => s.DialogFrame.TitleText);

        _detailView = new TextView
        {
            Text = LookupDetail(MergeStrategy.Default),
            VerticalTextAlignment = TextAlignment.Center,
        };
        _detailView.BindThemedTextColor(s => s.DialogBody.RowTextMissing);

        var chevron = new TextView
        {
            Text = LucideIcons.ChevronDown,
            FontFamily = LucideIcons.FontFamily,
            FontSize = 12,
            VerticalTextAlignment = TextAlignment.Center,
            HorizontalTextAlignment = TextAlignment.Center,
            Width = 16,
        };
        chevron.BindThemedTextColor(s => s.DialogBody.RowText);

        var row = new FlexRowView
        {
            Gap = 10,
            CrossAxisAlignment = CrossAxisAlignment.Center,
            Children =
            {
                _labelView,
                new FlexItem { Grow = 1, Child = _detailView },
                chevron,
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
            _labelView.Text = LookupLabel(s);
            _detailView.Text = LookupDetail(s);
        });
    }

    protected override void OnClicked()
    {
        var ctx = Context;
        if (ctx == null) return;
        var items = new List<RepoBarContextMenu.Item>(Options.Length);
        foreach (var opt in Options)
        {
            var strategy = opt.Strategy;
            items.Add(new RepoBarContextMenu.Item(
                $"{opt.Label} — {opt.Detail}",
                () => SelectedState.Value = strategy));
        }
        RepoBarContextMenu.Show(ctx, Position.BottomLeft, items);
    }

    private static string LookupLabel(MergeStrategy s)
    {
        foreach (var o in Options) if (o.Strategy == s) return o.Label;
        return string.Empty;
    }

    private static string LookupDetail(MergeStrategy s)
    {
        foreach (var o in Options) if (o.Strategy == s) return o.Detail;
        return string.Empty;
    }
}
