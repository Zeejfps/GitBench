using ZGF.Gui;
using ZGF.Gui.Desktop.Components.ContextMenu;
using ZGF.Gui.Views;

namespace GitBench;

public sealed class AddRepoButton : HoverableButton
{
    public AddRepoButton()
    {
        Height = 30;

        var label = new TextView
        {
            Text = "+  Add Repository",
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
        };
        label.BindThemedTextColor(s => s.Palette.TextSecondary);

        var background = new RectView
        {
            BorderSize = BorderSizeStyle.All(1),
            BorderRadius = BorderRadiusStyle.All(6),
            Children = { label }
        };
        BorderedButtonChrome.Bind(background, IsHovered);
        SetBackground(background);
    }

    protected override void OnClicked()
    {
        var ctx = Context;
        if (ctx == null) return;

        var items = new List<RepoBarContextMenu.Item>
        {
            new("Open from Folder…", OpenFromFolder, Icon: LucideIcons.FolderOpen),
            new("Clone Repository…", ShowCloneDialog, Icon: LucideIcons.FolderGit2),
        };
        // Anchor at the button's top edge and grow upward — the button lives at the very
        // bottom of the sidebar, so a downward menu would spill off-screen and get clamped
        // back over the button.
        RepoBarContextMenu.Show(ctx, Position.TopLeft, items, MenuPlacement.Above);
    }

    private void OpenFromFolder()
    {
        var path = Context?.Get<IPlatformShell>()?.PickFolder("Open Repository");
        if (string.IsNullOrEmpty(path)) return;
        Context?.Get<IRepoRegistry>()?.Open(path);
    }

    private void ShowCloneDialog()
        => Context?.Get<IMessageBus>()?.Broadcast(
            new ShowDialogMessage(onClose => new CloneRepoDialog(onClose)));
}
