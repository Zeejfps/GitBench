using GitBench.Localization;
using GitBench.Messages;
using GitBench.Widgets;
using ZGF.Desktop;
using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Desktop;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Repos;

/// <summary>Owns the native icon editor window and its unsaved draft, like SettingsWindowHost.</summary>
internal sealed record RepoIconWindowHost : Widget
{
    private const int ShadowPadding = 48;

    protected override View CreateView(Context ctx)
    {
        var view = new ContainerView { Width = 0, Height = 0 };
        view.Behaviors.Add(new Presenter(ctx));
        return view;
    }

    private sealed class Presenter(Context context) : IViewBehavior
    {
        private readonly IMessageBus _bus = context.Require<IMessageBus>();
        private ISecondaryWindow? _window;

        public void Attach(View view) => _bus.Subscribe<OpenRepoIconWindowMessage>(Open);

        public void Detach(View view)
        {
            _bus.Unsubscribe<OpenRepoIconWindowMessage>(Open);
            _window?.Close();
            _window = null;
        }

        private void Open(OpenRepoIconWindowMessage message)
        {
            if (_window is { } existing)
            {
                existing.Window.Show();
                existing.Window.Focus();
                return;
            }

            var size = context.Require<IWindowCoordinates>().ToScreenPoints(new CanvasRect(
                0, 0, RepoCustomizeIconDialog.DialogWidth + ShadowPadding * 2,
                RepoCustomizeIconDialog.DialogHeight + ShadowPadding * 2));
            ISecondaryWindow? opened = null;
            opened = context.Require<ISecondaryWindowFactory>().Open(new SecondaryWindowRequest
            {
                Title = context.Localization().Strings.Value.ReposCustomizeIconTitle,
                Width = size.Width,
                Height = size.Height,
                IsUndecorated = true,
                IsModal = true,
                CenterOnMainWindow = true,
                BuildRoot = ctx => Direction.Wrap(new Padding
                {
                    Amount = PaddingStyle.All(ShadowPadding),
                    Children =
                    [
                        new RepoCustomizeIconDialog { Repo = message.Repo, OnClose = () => opened?.Close() }
                            .WithController((c, _) => new WindowDragController(c.Require<IWindow>(), c.Require<InputSystem>())),
                    ],
                }).BuildView(ctx),
            });
            _window = opened;
            opened.Closed += () =>
            {
                if (ReferenceEquals(_window, opened)) _window = null;
            };
        }
    }
}
