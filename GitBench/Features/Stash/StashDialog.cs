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
            Height = 520f,
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
                new Grow { Child = new Raw { View = BuildFileList(ctx, vm) } },
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
        var column = new ColumnView { Gap = Spacing.None };

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
            for (var i = 0; i < files.Count; i++)
            {
                var index = i;
                var file = files[i];
                column.Children.Add(DialogFileRow.Build(
                    ctx, file.Display, file.Path, vm.CheckedPaths,
                    modifiers => vm.ClickRow(index, modifiers)));
            }
        }

        return new DialogScrollList { Content = column }.BuildView(ctx);
    }
}
