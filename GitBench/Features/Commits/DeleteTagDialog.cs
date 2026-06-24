using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Messages;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
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

        var s = ctx.Localization().Strings.Value;
        return new Dialog
        {
            Title = s.CommitsDeleteTagTitle,
            OnClose = OnClose,
            ViewModel = vm,
            Action = (s.CommitsDeleteTagAction, DialogButtonRole.Destructive),
            Command = vm.Delete,
            ConfirmKeys = true,
            Body =
            [
                new Text
                {
                    Value = s.CommitsDeleteTagDesc,
                    Wrap = TextWrap.Wrap,
                    Color = Theme.Color(t => t.DialogBody.BodyText),
                },
                new LabeledRow { Label = s.CommitsDeleteTagLabel, Value = TagValue(TagName) },
                new CheckboxWidget
                {
                    Label = s.CommitsDeleteTagRemoteCheckbox,
                    Checked = vm.DeleteFromRemotes,
                    Height = Sizes.RowHeight,
                }.WithController<KbmController>(),
            ],
        };
    }

    private static IWidget TagValue(string tagName) => new Row
    {
        Gap = Spacing.Md,
        CrossAxis = CrossAxisAlignment.Center,
        Children =
        [
            new Text
            {
                Value = LucideIcons.Tag,
                FontFamily = LucideIcons.FontFamily,
                FontSize = FontSize.Default,
                Width = Sizes.Icon,
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
