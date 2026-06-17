using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Git;
using GitBench.Messages;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Commits;

/// <summary>
/// Confirmation modal for deleting a tag. Runs `git tag -d &lt;name&gt;` locally and, when the
/// "delete from remote repositories" toggle is set, also removes it from every configured
/// remote (`git push &lt;remote&gt; --delete refs/tags/&lt;name&gt;`). Mirrors the Branches view's
/// delete dialogs.
/// </summary>
internal sealed record DeleteTagDialog : Widget
{
    public required Repo Repo { get; init; }
    public required string TagName { get; init; }
    public required Action OnClose { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var vm = new DeleteTagDialogViewModel(
            new DeleteTagRequest(Repo, TagName),
            ctx.Require<IGitService>(),
            ctx.Require<IUiDispatcher>(),
            ctx.Require<IMessageBus>());

        return new Dialog
        {
            Title = "Delete tag",
            OnClose = OnClose,
            ViewModel = vm,
            Action = ("Delete Tag", DialogButtonRole.Destructive),
            Command = vm.Delete,
            ConfirmKeys = true,
            Body =
            [
                new Text
                {
                    Value = "Delete tag from your repository",
                    Wrap = TextWrap.Wrap,
                    Color = Theme.Color(s => s.DialogBody.BodyText),
                },
                new LabeledRow { Label = "Tag:", Value = TagValue(TagName) },
                new Checkbox
                {
                    Label = "Delete tag from remote repositories",
                    Value = vm.DeleteFromRemotes,
                    Height = 22,
                },
            ],
        };
    }

    private static IWidget TagValue(string tagName) => new Row
    {
        Gap = 8,
        CrossAxis = CrossAxisAlignment.Center,
        Children =
        [
            new Text
            {
                Value = LucideIcons.Tag,
                FontFamily = LucideIcons.FontFamily,
                FontSize = 14,
                Width = 16,
                HAlign = TextAlignment.Center,
                VAlign = TextAlignment.Center,
                Color = Theme.Color(s => s.DialogBody.BodyText),
            },
            new Grow
            {
                Child = new Clipped
                {
                    Child = new Text
                    {
                        Value = tagName,
                        VAlign = TextAlignment.Center,
                        Wrap = TextWrap.NoWrap,
                        Color = Theme.Color(s => s.DialogFrame.TitleText),
                    },
                },
            },
        ],
    };
}
