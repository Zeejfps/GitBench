using GitBench.Controls.Dialogs;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Components.TextInput;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.ChangeSets;

/// <summary>
/// "Start change set…" dialog (Phase 4.1), reached from the group-header and repo context menus. Names
/// a branch and, for each primary in the group, offers a checkbox (include this repo) and a start-point
/// field (defaulting to that repo's default-branch tip). On confirm it loops
/// <see cref="ChangeSetOperations.CreateInAll"/> — <c>CreateBranch(repo, name, startPoint, checkout: true)</c>
/// per selected member — with the same per-repo outcome + summary toast as the other batch ops. The
/// MergeBranchDialog/CreateBranchDialog pattern: a widget over a small dialog view model.
/// </summary>
internal sealed record StartChangeSetDialog : Widget
{
    // The group's primaries, pre-resolved at menu-build time. The checklist defaults to all of them.
    public required IReadOnlyList<Repo> Repos { get; init; }
    public required Action OnClose { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var vm = new StartChangeSetDialogViewModel(
            Repos,
            ctx.Require<ChangeSetOperations>(),
            ctx.Require<IGitService>(),
            ctx.Require<IUiDispatcher>(),
            ctx.Require<ILocalizationService>());

        var s = ctx.Localization().Strings.Value;

        var body = new List<IWidget>
        {
            new LabeledInput
            {
                Label = s.ChangesetsStartNameLabel,
                Value = vm.Name,
                Status = vm.NameStatus,
                Placeholder = "feature/…",
            },
            HeaderRow(s),
        };
        foreach (var row in vm.Rows)
            body.Add(RepoRow(ctx, row));

        return new Dialog
        {
            Title = s.ChangesetsStartTitle,
            OnClose = OnClose,
            ViewModel = vm,
            Width = DialogFrame.WidthWide,
            Action = (s.CommonCreate, DialogButtonRole.Primary),
            Command = vm.Create,
            Body = body.ToArray(),
        };
    }

    // Two-column header naming the repo checklist and the start-point column.
    private static IWidget HeaderRow(Strings s) => new Row
    {
        Gap = Spacing.Md,
        CrossAxis = CrossAxisAlignment.Center,
        Children =
        [
            new Grow
            {
                Child = new Text
                {
                    Value = s.ChangesetsStartReposLabel,
                    Color = Theme.Color(t => t.DialogBody.SectionHeaderText),
                },
            },
            new Box
            {
                Width = StartPointColumnWidth,
                Children =
                [
                    new Text
                    {
                        Value = s.ChangesetsStartStartPointLabel,
                        Color = Theme.Color(t => t.DialogBody.SectionHeaderText),
                    },
                ],
            },
        ],
    };

    private const float StartPointColumnWidth = 200f;

    // One member row: a name checkbox (clicking the name toggles inclusion) and its start-point field.
    private static IWidget RepoRow(Context ctx, StartChangeSetDialogViewModel.RepoRow row) => new Row
    {
        Gap = Spacing.Md,
        CrossAxis = CrossAxisAlignment.Center,
        Height = Sizes.ControlHeight,
        Children =
        [
            new Grow
            {
                Child = new CheckboxWidget
                {
                    Checked = row.Included,
                    Label = row.DisplayName,
                    Height = Sizes.RowHeight,
                }.WithController<KbmController>(),
            },
            new Box
            {
                Width = StartPointColumnWidth,
                Children = [new Raw { View = StartPointField(ctx, row.StartPoint) }],
            },
        ],
    };

    // A bare bordered text input bound to the row's start point, registered with the dialog so Enter
    // submits / Esc cancels / Tab cycles like the name field.
    private static View StartPointField(Context ctx, State<string> value)
    {
        var input = DialogFrame.TextInput(ctx);
        input.BindTwoWay(value);
        ctx.Get<DialogInputRegistry>()?.Register(input, selectAllOnOpen: false);
        return DialogFrame.WrapInput(ctx, input);
    }
}
