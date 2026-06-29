using GitBench.Controls.Dialogs;
using GitBench.Features.LocalChanges;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Messages;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Controllers;
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
    // Rows the file list shows before it scrolls internally; past this the card stops growing.
    private const int MaxVisibleRows = 11;

    public required Repo Repo { get; init; }
    public required Action OnClose { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var vm = new StashDialogViewModel(
            new StashRequest(Repo),
            ctx.Require<IGitService>(),
            ctx.Require<IUiDispatcher>(),
            ctx.Require<IMessageBus>(),
            ctx.Require<LocalChangesSelectionStore>(),
            ctx.Localization());

        var message = new State<string>(vm.Message.Value);
        message.Changed += vm.SetMessage;

        var keepStaged = new State<bool>(vm.KeepStaged.Value);
        keepStaged.Changed += vm.SetKeepStaged;

        var s = ctx.Localization().Strings.Value;
        return new Dialog
        {
            Title = s.StashTitle,
            OnClose = OnClose,
            ViewModel = vm,
            Width = DialogFrame.WidthWide,
            BodyGap = 10,
            Action = (s.StashAction, DialogButtonRole.Primary),
            Command = vm.Stash,
            Body =
            [
                new LabeledInput
                {
                    Label = s.CommonMessage,
                    Value = message,
                },
                new Text
                {
                    Value = Prop.Bind(vm.FilesHeader),
                    Color = Theme.Color(t => t.DialogBody.SectionHeaderText),
                },
                new Raw { View = BuildFileList(ctx, vm) },
                new CheckboxWidget
                {
                    Label = s.StashKeepStagedCheckbox,
                    Checked = keepStaged,
                    Height = Sizes.RowHeight,
                }.WithController<KbmController>(),
            ],
        };
    }

    private static View BuildFileList(Context ctx, StashDialogViewModel vm)
    {
        var theme = ctx.Theme();
        var column = new ColumnView { Gap = Spacing.Hair };

        var files = vm.Files.Value;
        if (files.Count == 0)
        {
            var empty = new TextView(ctx.Canvas)
            {
                Text = ctx.Localization().Strings.Value.StashDialogNoChanges,
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

        // See DiscardChangesDialog: the frame's own scroll region lays the body out at its natural
        // height, so a Grow can't bound this list. An explicit height (honored at measure time,
        // unlike MaxHeightConstraint) caps the card to MaxVisibleRows and scrolls internally past
        // that, while hugging its rows when there are fewer.
        var visibleRows = Math.Min(Math.Max(files.Count, 1), MaxVisibleRows);
        var listHeight = visibleRows * Sizes.RowHeight + (visibleRows - 1) * Spacing.Hair;

        var fileScrollHost = new RectView
        {
            Height = listHeight + 2 * Spacing.Sm + 2f,
            BorderSize = BorderSizeStyle.All(1),
            BorderRadius = BorderRadiusStyle.All(Radius.Sm),
            Children =
            {
                new PaddingView
                {
                    Padding = PaddingStyle.All(Spacing.Sm),
                    Children =
                    {
                        new DialogScrollRegion { Content = new Raw { View = column } }.BuildView(ctx),
                    },
                },
            },
        };
        fileScrollHost.BindBackgroundColor(() => theme.Styles.Value.DialogFrame.InsetBackground);
        fileScrollHost.BindBorderColor(() => BorderColorStyle.All(theme.Styles.Value.DialogFrame.Border));

        return fileScrollHost;
    }

    private static View BuildRow(Context ctx, StashDialogViewModel vm, StashFileRow file)
    {
        var theme = ctx.Theme();
        var badge = new FileStatusBadge { Status = file.Display.Status }.BuildView(ctx);

        var pathText = new TextView(ctx.Canvas)
        {
            Text = FileChangeFormatting.FormatPath(file.Display),
            VerticalTextAlignment = TextAlignment.Center,
            TextOverflow = TextOverflow.Ellipsis,
        };
        pathText.BindTextColor(() => theme.Styles.Value.FileChangeRow.RowText);

        var rowContent = new FlexRowView
        {
            Gap = Spacing.Md,
            CrossAxisAlignment = CrossAxisAlignment.Center,
            Children =
            {
                badge,
                new FlexItem { Grow = 1, Child = pathText },
            },
        };

        var isChecked = new State<bool>(vm.CheckedPaths.Value.Contains(file.Path));
        isChecked.Changed += _ => vm.ToggleFile(file.Path);
        return new CheckboxWidget
        {
            Content = new Raw { View = rowContent },
            Checked = isChecked,
            Height = Sizes.RowHeight,
        }.WithController<KbmController>().BuildView(ctx);
    }
}
