using GitBench.Controls.Dialogs;
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

namespace GitBench.Features.LocalChanges;

/// <summary>
/// Confirmation modal for discarding unstaged changes. Lists every unstaged path with a
/// checkbox — the paths the user had selected when they invoked Discard come pre-checked —
/// so they can fine-tune the set before committing to the throw-away. Discard is a
/// destructive action: the worktree changes (and any untracked files in the set) cannot
/// be recovered from git afterwards.
/// </summary>
internal sealed record DiscardChangesDialog : Widget
{
    public required Repo Repo { get; init; }
    public required IReadOnlyList<string> Paths { get; init; }
    public required Action OnClose { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var vm = new DiscardChangesViewModel(
            new DiscardChangesRequest(Repo, Paths),
            ctx.Require<IGitService>(),
            ctx.Require<IUiDispatcher>(),
            ctx.Require<IMessageBus>(),
            ctx.Localization());

        var s = ctx.Localization().Strings.Value;
        return new Dialog
        {
            ViewModel = vm,
            Title = s.LocalchangesDiscardDialogTitle,
            OnClose = OnClose,
            Width = DialogFrame.WidthWide,
            Height = 480f,
            BodyGap = 10,
            Action = (s.CommonDiscard, DialogButtonRole.Destructive),
            Command = vm.Discard,
            ConfirmKeys = true,
            Body =
            [
                new Text
                {
                    Value = s.LocalchangesDiscardDialogBody,
                    Wrap = TextWrap.Wrap,
                    Color = Theme.Color(t => t.DialogBody.BodyText),
                },
                new Text
                {
                    Value = Prop.Bind(vm.FilesHeader),
                    Color = Theme.Color(t => t.DialogBody.SectionHeaderText),
                },
                new Grow { Child = new Raw { View = BuildFileList(ctx, vm) } },
            ],
        };
    }

    private static View BuildFileList(Context ctx, DiscardChangesViewModel vm)
    {
        var theme = ctx.Theme();
        var column = new ColumnView { Gap = Spacing.None };

        var files = vm.Files.Value;
        if (files.Count == 0)
        {
            var empty = new TextView(ctx.Canvas)
            {
                Text = ctx.Localization().Strings.Value.LocalchangesDiscardDialogNoChanges,
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

public readonly record struct DiscardChangesRequest(Repo Repo, IReadOnlyList<string> Paths);
