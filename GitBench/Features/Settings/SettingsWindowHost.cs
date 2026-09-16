using GitBench.Controls;
using GitBench.Controls.Dialogs;
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

namespace GitBench.Features.Settings;

/// <summary>Owns the single settings window and its edit session for the lifetime of the app view.</summary>
internal sealed record SettingsWindowHost : Widget
{
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

        public void Attach(View view) => _bus.Subscribe<OpenSettingsWindowMessage>(Open);

        public void Detach(View view)
        {
            _bus.Unsubscribe<OpenSettingsWindowMessage>(Open);
            _window?.Close();
            _window = null;
        }

        private void Open(OpenSettingsWindowMessage message)
        {
            if (_window is { } existing)
            {
                existing.Window.Show();
                existing.Window.Focus();
                return;
            }

            var size = context.Require<IWindowCoordinates>().ToScreenPoints(
                new CanvasRect(0, 0, DialogFrame.WidthWide, SettingsDialog.DialogHeight));
            ISecondaryWindow? opened = null;
            opened = context.Require<ISecondaryWindowFactory>().Open(new SecondaryWindowRequest
            {
                Title = context.Localization().Strings.Value.SettingsTitle,
                Width = size.Width,
                Height = size.Height,
                IsUndecorated = true,
                CenterOnMainWindow = true,
                BuildRoot = ctx => Direction.Wrap(new SettingsDialog
                {
                    OnClose = () => opened?.Close(),
                    HostedInWindow = true,
                }.WithController<DialogKbmController>()
                    .WithController((c, _) => new WindowDragController(c.Require<IWindow>(), c.Require<InputSystem>())
                    {
                        // Scroll viewports handle the wheel but their blank space can still move the window.
                        IsBackgroundController = controller => controller is WheelScrollController,
                    })).BuildView(ctx),
            });
            _window = opened;
            opened.Closed += () =>
            {
                if (ReferenceEquals(_window, opened)) _window = null;
            };
        }
    }
}
