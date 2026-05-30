using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.VerticalScrollBar;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitGui;

/// <summary>
/// Confirmation modal for discarding unstaged changes. Lists every unstaged path with a
/// checkbox — the paths the user had selected when they invoked Discard come pre-checked —
/// so they can fine-tune the set before committing to the throw-away. Discard is a
/// destructive action: the worktree changes (and any untracked files in the set) cannot
/// be recovered from git afterwards.
/// </summary>
internal sealed class DiscardChangesDialog : MultiChildView, IBind<DiscardChangesViewModel>
{
    private readonly Action _onClose;
    private readonly DialogButton _discardButton;
    private readonly DialogButton _cancelButton;
    private readonly TextView _errorView;
    private readonly ColumnView _fileListColumn;
    private readonly TextView _fileListHeader;
    private readonly TextView _fileListEmpty;
    private DiscardChangesViewModel? _vm;

    public DiscardChangesDialog(Repo repo, IReadOnlyList<string> paths, Action onClose)
    {
        Width = 520f;
        Height = 480f;

        _onClose = onClose;

        var prompt = new TextView
        {
            Text = "Discarding cannot be undone. Choose the changes to discard.",
            TextWrap = TextWrap.Wrap,
        };
        prompt.BindThemedTextColor(s => s.DialogBody.BodyText);

        _fileListHeader = DialogFrame.Label("Files");

        _fileListEmpty = new TextView
        {
            Text = "No unstaged changes.",
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

        _errorView = DialogFrame.ErrorView();

        _cancelButton = new DialogButton("Cancel", onClose) { Height = DialogFrame.DefaultButtonHeight };
        _discardButton = new DialogButton("Discard") { Height = DialogFrame.DefaultButtonHeight };

        AddChildToSelf(DialogFrame.Build("Discard changes", onClose, new FlexColumnView
        {
            Gap = 10,
            CrossAxisAlignment = CrossAxisAlignment.Stretch,
            Children =
            {
                prompt,
                _fileListHeader,
                new FlexItem { Grow = 1, Child = fileScrollHost },
                _errorView,
                DialogFrame.ButtonsRow(_cancelButton, _discardButton),
            },
        }));

        this.UseController(_ => new DialogKbmController(_discardButton.Command, _onClose));

        var request = new DiscardChangesRequest(repo, paths);
        this.UseViewModel(
            ctx => new DiscardChangesViewModel(
                request,
                ctx.Require<IGitService>(),
                ctx.Require<IUiDispatcher>(),
                ctx.Require<IMessageBus>()),
            Bind);
    }

    public void Bind(DiscardChangesViewModel vm)
    {
        _vm = vm;
        vm.CloseRequested += _onClose;

        _discardButton.BindBusyCommand(vm.Discard);
        _cancelButton.DisableWhile(vm.Discard.IsRunning);
        _errorView.BindText(vm.Discard.Error, s => s ?? string.Empty);
        _fileListHeader.BindText(vm.FilesHeader);

        vm.Files.Subscribe(RenderFiles);
    }

    private void RenderFiles(IReadOnlyList<DiscardFileRow> files)
    {
        _fileListColumn.Children.Clear();

        if (files.Count == 0)
        {
            _fileListColumn.Children.Add(_fileListEmpty);
            return;
        }

        foreach (var file in files)
            _fileListColumn.Children.Add(BuildRow(file));
    }

    private CheckboxView BuildRow(DiscardFileRow file)
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

        return checkbox;
    }
}

public readonly record struct DiscardChangesRequest(Repo Repo, IReadOnlyList<string> Paths);
