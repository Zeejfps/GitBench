using GitBench.Controls;
using GitBench.Messages;
using GitBench.Platform;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Components.ContextMenu;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Repos;

public sealed record AddRepoButton : Widget
{
    // CreateView (not Build) so the click handler can anchor the menu at the button's own rect.
    protected override View CreateView(Context ctx)
    {
        View? button = null;
        var state = new ButtonState(new Command(() => ShowMenu(ctx, button!)));

        button = new Box
        {
            Height = 30,
            BorderSize = BorderSizeStyle.All(1),
            BorderRadius = BorderRadiusStyle.All(6),
            Background = Theme.Color(s =>
                state.Hovered.Value ? s.BorderedButton.BackgroundHover : s.BorderedButton.BackgroundIdle),
            BorderColor = Theme.BorderColor(s => BorderColorStyle.All(
                state.Hovered.Value ? s.BorderedButton.BorderHover : s.BorderedButton.BorderIdle)),
            Children =
            [
                new Text
                {
                    Value = "+  Add Repository",
                    HAlign = TextAlignment.Center,
                    VAlign = TextAlignment.Center,
                    Color = Theme.Color(s => s.Palette.TextSecondary),
                },
            ],
        }.WithController<KbmController>(state).BuildView(ctx);

        return button;
    }

    // Anchor at the button's top edge and grow upward — the button lives at the very bottom of the
    // sidebar, so a downward menu would spill off-screen and get clamped back over the button.
    private static void ShowMenu(Context ctx, View anchor)
    {
        var items = new List<RepoBarContextMenu.Item>
        {
            new("Open from Folder…", () => OpenFromFolder(ctx), Icon: LucideIcons.FolderOpen),
            new("Clone Repository…", () => ShowCloneDialog(ctx), Icon: LucideIcons.FolderGit2),
        };
        RepoBarContextMenu.Show(ctx, anchor.Position.TopLeft, items, MenuPlacement.Above);
    }

    private static void OpenFromFolder(Context ctx)
    {
        var path = ctx.Get<IPlatformShell>()?.PickFolder("Open Repository");
        if (string.IsNullOrEmpty(path)) return;
        ctx.Get<IRepoRegistry>()?.Open(path);
    }

    private static void ShowCloneDialog(Context ctx)
        => ctx.Get<IMessageBus>()?.Broadcast(
            new ShowDialogMessage(onClose => new CloneRepoDialog { OnClose = onClose }));
}
