using GitBench.Controls;
using GitBench.Messages;
using GitBench.Platform;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Components.ContextMenu;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Repos;

internal sealed record AddRepoButton : Widget
{
    // Anchor at the button's top edge and grow upward — the button lives at the very bottom of the
    // sidebar, so a downward menu would spill off-screen and get clamped back over the button.
    protected override IWidget Build(Context ctx) => new AddRepoButtonLook()
        .WithMenuController(rect => RepoBarContextMenu.Show(ctx, rect.TopLeft, BuildItems(ctx), MenuPlacement.Above));

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

// The look: an outline button with a centered label. The menu behavior is bolted on by
// AddRepoButton via WithMenuController, so this stays a dumb pressable.
file sealed record AddRepoButtonLook : Widget<ButtonState>
{
    protected override ButtonState CreateState(Context ctx) => new();

    protected override IWidget Build(Context ctx, ButtonState state) => new Box
    {
        Height = 30,
        BorderSize = BorderSizeStyle.All(1),
        BorderRadius = BorderRadiusStyle.All(6),
        Background = Theme.Color(s => s.BorderedButton.Surface(state)),
        BorderColor = Theme.BorderColor(s => BorderColorStyle.All(s.BorderedButton.Border(state))),
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
    };
}
