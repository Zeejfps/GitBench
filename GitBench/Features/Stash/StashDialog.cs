using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Features.LocalChanges;
using GitBench.Features.Operations;
using GitBench.Git;
using GitBench.Messages;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.VerticalScrollBar;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Stash;

// Modal shown when the user clicks Stash in the actions toolbar. Lets the user name the
// stash, pick the files to stash, and optionally keep the index (--keep-index) so staged
// hunks stay around after stashing. --include-untracked is derived from the row checks:
// passed iff any selected row is an untracked file.
internal sealed record StashDialog : Widget
{
    public required Repo Repo { get; init; }
    public required Action OnClose { get; init; }

    protected override View CreateView(Context ctx)
    {
        var vm = new StashDialogViewModel(
            new StashRequest(Repo),
            ctx.Require<IGitService>(),
            ctx.Require<IUiDispatcher>(),
            ctx.Require<IMessageBus>(),
            ctx.Require<LocalChangesSelectionStore>());

        var message = new State<string>(vm.Message.Value);
        message.Changed += vm.SetMessage;

        var keepStaged = new State<bool>(vm.KeepStaged.Value);
        keepStaged.Changed += vm.SetKeepStaged;

        var view = new Dialog
        {
            Title = "Stash changes",
            OnClose = OnClose,
            Width = DialogFrame.WidthWide,
            Height = 520f,
            BodyGap = 10,
            Action = ("Stash", DialogButtonRole.Primary),
            Command = vm.Stash,
            Body =
            [
                new LabeledInput
                {
                    Label = "Message",
                    Value = message,
                },
                new ThemedText
                {
                    Bind = () => vm.FilesHeader.Value,
                    Color = s => s.DialogBody.SectionHeaderText,
                },
                new Grow { Child = new Raw { View = BuildFileList(ctx, vm) } },
                new Checkbox
                {
                    Label = "Keep staged changes in index",
                    Value = keepStaged,
                    Height = 22,
                },
            ],
        }.BuildView(ctx);

        view.UseViewModel(() => vm, v => v.CloseRequested += OnClose);
        return view;
    }

    private static View BuildFileList(Context ctx, StashDialogViewModel vm)
    {
        var theme = ctx.Theme();
        var column = new ColumnView { Gap = 1 };

        var files = vm.Files.Value;
        if (files.Count == 0)
        {
            var empty = new TextView(ctx.Canvas)
            {
                Text = "No local changes.",
                HorizontalTextAlignment = TextAlignment.Center,
                VerticalTextAlignment = TextAlignment.Center,
            };
            empty.BindTextColor(() => theme.Styles.Value.FileChangesSection.EmptyPlaceholderText);
            column.Children.Add(empty);
        }
        else
        {
            foreach (var file in files)
                column.Children.Add(BuildRow(ctx, vm, file));
        }

        var scrollPane = new VerticalScrollPane();
        scrollPane.Children.Add(column);
        scrollPane.UseController(ctx.Require<InputSystem>(),
            () => new VerticalScrollPaneWheelController(scrollPane));

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
        fileScrollHost.BindBackgroundColor(() => theme.Styles.Value.DialogFrame.InsetBackground);
        fileScrollHost.BindBorderColor(() => BorderColorStyle.All(theme.Styles.Value.DialogFrame.Border));
        fileScrollHost.Use(() => new VerticalScrollBarSyncController(scrollPane, vScrollBar));

        return fileScrollHost;
    }

    private static View BuildRow(Context ctx, StashDialogViewModel vm, StashFileRow file)
    {
        var theme = ctx.Theme();
        var badge = FileChangesUI.CreateStatusBadge(ctx, file.Display);

        var pathText = new TextView(ctx.Canvas)
        {
            Text = FileChangeFormatting.FormatPath(file.Display),
            VerticalTextAlignment = TextAlignment.Center,
        };
        pathText.BindTextColor(() => theme.Styles.Value.FileChangeRow.RowText);

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
        checkbox.IsChecked.Value = vm.CheckedPaths.Value.Contains(file.Path);
        checkbox.IsChecked.Changed += _ => vm.ToggleFile(file.Path);
        return checkbox;
    }
}
