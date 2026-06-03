using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.VerticalScrollBar;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitGui;

// Modal shown when the user clicks Stash in the actions toolbar. Lets the user name the
// stash, pick the files to stash, and optionally keep the index (--keep-index) so staged
// hunks stay around after stashing. --include-untracked is derived from the row checks:
// passed iff any selected row is an untracked file.
internal sealed class StashDialog : MultiChildView, IBind<StashDialogViewModel>
{
    private readonly Action _onClose;
    private readonly LabeledInputField _messageField;
    private readonly DialogShell _shell;
    private readonly CheckboxView _keepStagedCheckbox;
    private readonly ColumnView _fileListColumn;
    private readonly TextView _fileListHeader;
    private readonly TextView _fileListEmpty;
    private readonly List<FileRow> _rows = new();
    private StashDialogViewModel? _vm;

    public StashDialog(Repo repo, Action onClose)
    {
        Height = 520f;

        _onClose = onClose;

        _messageField = new LabeledInputField("Message");

        _keepStagedCheckbox = new CheckboxView("Keep staged changes in index")
        {
            Height = 22,
        };

        _fileListHeader = DialogFrame.Label("Files");

        _fileListEmpty = new TextView
        {
            Text = "No local changes.",
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
        };
        _fileListEmpty.BindThemedTextColor(s => s.FileChangesSection.EmptyPlaceholderText);

        _fileListColumn = new ColumnView { Gap = 1 };

        var scrollPane = new VerticalScrollPane();
        scrollPane.Children.Add(_fileListColumn);
        scrollPane.UseController(_ => new VerticalScrollPaneWheelController(scrollPane));

        var vScrollBar = ScrollBars.CreateVertical();

        var fileScrollHost = new RectView
        {
            BorderSize = BorderSizeStyle.All(1),
            BorderRadius = BorderRadiusStyle.All(4),
            Padding = PaddingStyle.All(6),
            Children =
            {
                new BorderLayoutView
                {
                    Center = scrollPane,
                    East = vScrollBar,
                },
            },
        };
        fileScrollHost.BindThemedBackgroundColor(s => s.DialogFrame.InsetBackground);
        fileScrollHost.BindThemedBorderColor(s => BorderColorStyle.All(s.DialogFrame.Border));
        fileScrollHost.UsePresenter(_ => new VerticalScrollBarSyncController(scrollPane, vScrollBar));

        _shell = new DialogShell("Stash changes", onClose)
        {
            Width = DialogFrame.WidthWide,
            BodyGap = 10,
            Action = ("Stash", DialogButtonRole.Primary),
            Body =
            {
                _messageField,
                _fileListHeader,
                new FlexItem { Grow = 1, Child = fileScrollHost },
                _keepStagedCheckbox,
            },
        };
        AddChildToSelf(_shell.View);

        _shell.SubmitFrom(_messageField.Input);

        var request = new StashRequest(repo);
        this.UseViewModel(
            ctx => new StashDialogViewModel(
                request,
                ctx.Require<IGitService>(),
                ctx.Require<IUiDispatcher>(),
                ctx.Require<IMessageBus>(),
                ctx.Require<LocalChangesSelectionStore>()),
            Bind);
    }

    public void Bind(StashDialogViewModel vm)
    {
        _vm = vm;
        vm.CloseRequested += _onClose;
        vm.FocusMessageRequested += () => _shell.BeginEditing();

        _messageField.Input.BindTwoWay(vm.Message, vm.SetMessage);

        vm.KeepStaged.Subscribe(b => _keepStagedCheckbox.IsChecked.Value = b);
        _keepStagedCheckbox.IsChecked.Changed += b => vm.SetKeepStaged(b);

        _shell.BindCommand(vm.Stash);
        _fileListHeader.BindText(vm.FilesHeader);

        vm.Files.Subscribe(RenderFiles);

        vm.RequestFocusMessage();
    }

    private void RenderFiles(IReadOnlyList<StashFileRow> files)
    {
        _fileListColumn.Children.Clear();
        _rows.Clear();

        if (files.Count == 0)
        {
            _fileListColumn.Children.Add(_fileListEmpty);
            return;
        }

        foreach (var file in files)
        {
            var row = BuildRow(file);
            _rows.Add(row);
            _fileListColumn.Children.Add(row.View);
        }
    }

    private FileRow BuildRow(StashFileRow file)
    {
        var vm = _vm!;

        var badge = FileChangesUI.CreateStatusBadge(file.Display);

        var pathText = new TextView
        {
            Text = FileChangeFormatting.FormatPath(file.Display),
            VerticalTextAlignment = TextAlignment.Center,
        };
        pathText.BindThemedTextColor(s => s.FileChangeRow.RowText);

        var rowContent = new FlexRowView
        {
            Gap = 8f,
            CrossAxisAlignment = CrossAxisAlignment.Center,
            Children =
            {
                badge,
                new FlexItem { Grow = 1, Child = pathText },
            },
        };

        var checkbox = new CheckboxView(rowContent)
        {
            Height = 22,
        };
        // Seed from VM state BEFORE wiring Changed, so the initial paint doesn't trigger
        // a phantom toggle through the handler.
        checkbox.IsChecked.Value = vm.CheckedPaths.Value.Contains(file.Path);
        checkbox.IsChecked.Changed += _ => vm.ToggleFile(file.Path);

        return new FileRow(file, checkbox);
    }

    private sealed record FileRow(StashFileRow Row, CheckboxView View);
}
