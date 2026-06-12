using GitBench.Controls;
using GitBench.Messages;
using GitBench.Platform;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Components.ContextMenu;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Repos;

public sealed record AddRepoButton : Widget
{
    protected override View CreateView(Context ctx) => new ButtonView(ctx);

    private sealed class ButtonView : HoverableButton
    {
        private readonly Context _ctx;

        public ButtonView(Context ctx) : base(ctx)
        {
            _ctx = ctx;
            Height = 30;

            var theme = ctx.Theme();
            var label = new TextView(ctx.Canvas)
            {
                Text = "+  Add Repository",
                HorizontalTextAlignment = TextAlignment.Center,
                VerticalTextAlignment = TextAlignment.Center,
            };
            label.BindTextColor(() => theme.Styles.Value.Palette.TextSecondary);

            var background = new RectView
            {
                BorderSize = BorderSizeStyle.All(1),
                BorderRadius = BorderRadiusStyle.All(6),
                Children = { label }
            };
            BorderedButtonChrome.Bind(background, theme, IsHovered);
            SetBackground(background);
        }

        protected override void OnClicked()
        {
            var items = new List<RepoBarContextMenu.Item>
            {
                new("Open from Folder…", OpenFromFolder, Icon: LucideIcons.FolderOpen),
                new("Clone Repository…", ShowCloneDialog, Icon: LucideIcons.FolderGit2),
            };
            // Anchor at the button's top edge and grow upward — the button lives at the very
            // bottom of the sidebar, so a downward menu would spill off-screen and get clamped
            // back over the button.
            RepoBarContextMenu.Show(_ctx, Position.TopLeft, items, MenuPlacement.Above);
        }

        private void OpenFromFolder()
        {
            var path = _ctx.Get<IPlatformShell>()?.PickFolder("Open Repository");
            if (string.IsNullOrEmpty(path)) return;
            _ctx.Get<IRepoRegistry>()?.Open(path);
        }

        private void ShowCloneDialog()
            => _ctx.Get<IMessageBus>()?.Broadcast(
                new ShowDialogMessage(onClose => new CloneRepoDialog { OnClose = onClose }));
    }
}
