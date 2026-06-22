using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Git;
using GitBench.Messages;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Commits;

/// <summary>
/// Modal shown when the user picks "Create Tag" on a commit in the history. Mirrors Fork's
/// "Create Tag" dialog: the target commit, a tag name, an optional annotation message, and a
/// "push to all remotes" toggle. A non-empty message yields an annotated tag, otherwise a
/// lightweight one — see <see cref="IGitService.CreateTag"/>.
/// </summary>
internal sealed record CreateTagDialog : Widget
{
    public required Repo Repo { get; init; }
    public required string Sha { get; init; }
    public required string ShortSha { get; init; }
    public required string Summary { get; init; }
    public required Action OnClose { get; init; }

    protected override View CreateView(Context ctx)
    {
        var vm = new CreateTagDialogViewModel(
            new CreateTagRequest(Repo, Sha),
            ctx.Require<IGitService>(),
            ctx.Require<IUiDispatcher>(),
            ctx.Require<IMessageBus>());

        var messageField = new GrowingDescriptionField(ctx, 72f, 200f) { PlaceholderText = "optional" };
        messageField.BindTwoWay(vm.Message, vm.SetMessage);

        var shell = new DialogShell(ctx, "Create tag", OnClose)
        {
            Width = DialogFrame.WidthWide,
            Action = ("Create", DialogButtonRole.Primary),
        };

        IWidget[] body =
        [
            new Text
            {
                Value = "Create annotated tag",
                Wrap = TextWrap.Wrap,
                Color = Theme.Color(s => s.DialogBody.BodyText),
            },
            new LabeledRow { Label = "Create tag at:", Value = CommitValue(ctx, ShortSha, Summary) },
            // Each label sits tight against its field (small intra-group gap); the column's
            // larger Gap separates one section from the next so labels read as attached to
            // their inputs rather than floating midway between them.
            new LabeledInput
            {
                Label = "Tag name",
                Value = vm.Name,
                Placeholder = "Enter Tag Name",
                Status = vm.NameStatus,
            },
            new Column
            {
                Gap = 4,
                CrossAxis = CrossAxisAlignment.Stretch,
                Children =
                [
                    new Text
                    {
                        Value = "Message",
                        Color = Theme.Color(s => s.DialogBody.SectionHeaderText),
                    },
                    new Raw { View = messageField },
                ],
            },
            new CheckboxWidget { Label = "Push to all remotes", Checked = vm.PushToAllRemotes, Height = 22 }.WithController<KbmController>(),
        ];

        var inputs = new DialogInputRegistry();
        var bodyScope = new Context(ctx);
        bodyScope.AddService(inputs);
        foreach (var widget in body)
            shell.Body.Add(widget.BuildView(bodyScope));

        var root = new ContainerView();
        root.Children.Add(shell.View);

        shell.BindCommand(vm.Create);

        // Submit-on-enter / cancel-on-esc lives on the name input, not the dialog — see
        // CreateBranchDialog: the input controller consumes left-press inside its own view,
        // so attaching to the outer dialog would swallow clicks meant for the buttons. The
        // message field keeps its own multi-line controller so Enter inserts a newline there.
        shell.SubmitFrom(inputs.Entries.Select(e => e.Input).ToArray());

        // Reflect the toggle in the primary button's label, like Fork ("Create and Push").
        root.Bind(vm.PushToAllRemotes, push => shell.SetActionLabel(push ? "Create and Push" : "Create"));

        root.UseViewModel(() => vm, v =>
        {
            v.CloseRequested += OnClose;
            shell.BeginEditing();
        });
        return root;
    }

    private static IWidget CommitValue(Context ctx, string shortSha, string summary)
    {
        // Ellipsis (…) on overflow rather than NoWrap-in-a-ClippingView: the clip let the
        // single line run past the dialog's right edge instead of truncating it. Ellipsis
        // measures against the laid-out Grow width and cuts the text with a trailing "…".
        var theme = ctx.Theme();
        var summaryLabel = new TextView(ctx.Canvas)
        {
            Text = summary,
            VerticalTextAlignment = TextAlignment.Center,
            TextOverflow = TextOverflow.Ellipsis,
        };
        summaryLabel.BindTextColor(() => theme.Styles.Value.DialogBody.BodyText);

        return new Row
        {
            Gap = 8,
            CrossAxis = CrossAxisAlignment.Center,
            Children =
            [
                new Text
                {
                    Value = "●",
                    FontSize = 10,
                    Width = 16,
                    HAlign = TextAlignment.Center,
                    VAlign = TextAlignment.Center,
                    Color = Theme.Color(s => s.DialogBody.BodyText),
                },
                new Text
                {
                    Value = shortSha,
                    VAlign = TextAlignment.Center,
                    Color = Theme.Color(s => s.DialogFrame.TitleText),
                },
                new Grow { Child = new Raw { View = summaryLabel } },
            ],
        };
    }
}
