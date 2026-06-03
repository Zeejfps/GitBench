using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitGui;

/// <summary>
/// Confirmation modal for deleting a tag. Runs `git tag -d &lt;name&gt;` locally and, when the
/// "delete from remote repositories" toggle is set, also removes it from every configured
/// remote (`git push &lt;remote&gt; --delete refs/tags/&lt;name&gt;`). Mirrors the Branches view's
/// delete dialogs.
/// </summary>
internal sealed class DeleteTagDialog : MultiChildView, IBind<DeleteTagDialogViewModel>
{
    private readonly DialogShell _shell;
    private readonly CheckboxView _remoteCheckbox;
    private readonly Action _onClose;

    public DeleteTagDialog(Repo repo, string tagName, Action onClose)
    {
        _onClose = onClose;

        var subtitle = new TextView { Text = "Delete tag from your repository", TextWrap = TextWrap.Wrap };
        subtitle.BindThemedTextColor(s => s.DialogBody.BodyText);

        var tagRow = BuildLabeledRow("Tag:", BuildTagValue(tagName));

        _remoteCheckbox = new CheckboxView("Delete tag from remote repositories") { Height = 22 };

        _shell = new DialogShell("Delete tag", onClose)
        {
            Action = ("Delete Tag", DialogButtonRole.Destructive),
            Body = { subtitle, tagRow, _remoteCheckbox },
        };
        AddChildToSelf(_shell.View);

        _shell.AttachConfirmKeys(this);

        var request = new DeleteTagRequest(repo, tagName);
        this.UseViewModel(
            ctx => new DeleteTagDialogViewModel(
                request,
                ctx.Require<IGitService>(),
                ctx.Require<IUiDispatcher>(),
                ctx.Require<IMessageBus>()),
            Bind);
    }

    public void Bind(DeleteTagDialogViewModel vm)
    {
        vm.CloseRequested += _onClose;

        _remoteCheckbox.IsChecked.BindTwoWay(vm.DeleteFromRemotes);
        _shell.BindCommand(vm.Delete);
    }

    private static FlexRowView BuildLabeledRow(string label, MultiChildView value)
    {
        var labelText = new TextView { Text = label, VerticalTextAlignment = TextAlignment.Center };
        labelText.BindThemedTextColor(s => s.DialogBody.SectionHeaderText);
        return new FlexRowView
        {
            Gap = 10,
            CrossAxisAlignment = CrossAxisAlignment.Center,
            Height = 28,
            Children =
            {
                labelText,
                new FlexItem { Grow = 1, Child = value },
            },
        };
    }

    private static MultiChildView BuildTagValue(string tagName)
    {
        var icon = new TextView
        {
            Text = LucideIcons.Tag,
            FontFamily = LucideIcons.FontFamily,
            FontSize = 14,
            VerticalTextAlignment = TextAlignment.Center,
            HorizontalTextAlignment = TextAlignment.Center,
            Width = 16,
        };
        icon.BindThemedTextColor(s => s.DialogBody.BodyText);

        var nameLabel = new TextView
        {
            Text = tagName,
            VerticalTextAlignment = TextAlignment.Center,
            TextWrap = TextWrap.NoWrap,
        };
        nameLabel.BindThemedTextColor(s => s.DialogFrame.TitleText);

        return new FlexRowView
        {
            Gap = 8,
            CrossAxisAlignment = CrossAxisAlignment.Center,
            Children =
            {
                icon,
                new FlexItem { Grow = 1, Child = new ClippingView { Children = { nameLabel } } },
            },
        };
    }
}
