using GitBench.Controls;
using GitBench.Messages;
using GitBench.Platform;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Components.ContextMenu;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Repos;

internal sealed record AddRepoButton : Widget<ButtonState>
{
    protected override ButtonState CreateState(Context ctx) => new(null);

    protected override IWidget Build(Context ctx, ButtonState state)
    {
        return new Box
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
        }.WithController(ctx, v => new MenuButtonController(v, state,
            // Anchor at the button's top edge and grow upward — the button lives at the very bottom
            // of the sidebar, so a downward menu would spill off-screen and get clamped over it.
            anchor => RepoBarContextMenu.Show(ctx, anchor, BuildItems(ctx), MenuPlacement.Above)));
    }

    private static IReadOnlyList<RepoBarContextMenu.Item> BuildItems(Context ctx) =>
    [
        new("Open from Folder…", () => OpenFromFolder(ctx), Icon: LucideIcons.FolderOpen),
        new("Clone Repository…", () => ShowCloneDialog(ctx), Icon: LucideIcons.FolderGit2),
    ];

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
