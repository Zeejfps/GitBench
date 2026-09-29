using GitBench.App;
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
    // Leave transparent space for the dialog's blur beyond its rounded frame.
    private const int ShadowPadding = 48;
    private const int ResizeGrip = 8;
    private const float MinDialogWidth = 480f;
    private const float MinDialogHeight = 360f;

    protected override View CreateView(Context ctx)
    {
        var view = new ContainerView { Width = 0, Height = 0 };
        view.Behaviors.Add(new Presenter(ctx));
        return view;
    }

    private sealed class Presenter(Context context) : IViewBehavior
    {
        private readonly IMessageBus _bus = context.Require<IMessageBus>();
        private readonly PreferencesService _preferences = context.Require<PreferencesService>();
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

            var coordinates = context.Require<IWindowCoordinates>();
            var size = coordinates.ToScreenPoints(
                new CanvasRect(0, 0, SettingsDialog.DialogWidth + ShadowPadding * 2,
                    SettingsDialog.DialogHeight + ShadowPadding * 2));
            var minSize = coordinates.ToScreenPoints(
                new CanvasRect(0, 0, MinDialogWidth + ShadowPadding * 2, MinDialogHeight + ShadowPadding * 2));
            ISecondaryWindow? opened = null;
            opened = context.Require<ISecondaryWindowFactory>().Open(new SecondaryWindowRequest
            {
                Title = context.Localization().Strings.Value.SettingsTitle,
                Width = _preferences.Current.SettingsWindowWidth ?? size.Width,
                Height = _preferences.Current.SettingsWindowHeight ?? size.Height,
                IsUndecorated = true,
                IsModal = true,
                CenterOnMainWindow = true,
                BuildRoot = ctx => Direction.Wrap(new Stack
                {
                    Children =
                    [
                        new Padding
                        {
                            Amount = PaddingStyle.All(ShadowPadding),
                            Children =
                            [
                                new SettingsDialog
                                {
                                    OnClose = () => opened?.Close(),
                                    HostedInWindow = true,
                                    InitialPage = message.Page,
                                }.WithController<DialogKbmController>()
                                .WithController((c, _) => new WindowDragController(c.Require<IWindow>(), c.Require<InputSystem>())
                                {
                                    // Scroll viewports handle the wheel but their blank space can still move the window.
                                    IsBackgroundController = controller => controller is WheelScrollController,
                                }),
                            ],
                        },
                        ResizeGrips(minSize),
                    ],
                }).BuildView(ctx),
            });
            _window = opened;
            opened.Window.OnResize += (w, h) =>
            {
                if (w > 0 && h > 0) _preferences.Update(p => p with { SettingsWindowWidth = w, SettingsWindowHeight = h });
            };
            opened.Closed += () =>
            {
                if (ReferenceEquals(_window, opened)) _window = null;
            };
        }

        private static IWidget ResizeGrips(ScreenRect minSize)
        {
            IWidget Grip(WindowEdges edges, int? width = null) => new Box
            {
                Width = width is { } w ? w : default(Prop<float>),
            }.WithController((c, _) => new WindowResizeController(c.Require<IWindow>(), c.Require<InputSystem>(), edges)
            {
                MinWidth = minSize.Width,
                MinHeight = minSize.Height,
            });

            IWidget EdgeRow(WindowEdges edge) => new Row
            {
                Height = ResizeGrip,
                CrossAxis = CrossAxisAlignment.Stretch,
                Children =
                [
                    Grip(edge | WindowEdges.Left, width: ResizeGrip),
                    new Grow { Child = Grip(edge) },
                    Grip(edge | WindowEdges.Right, width: ResizeGrip),
                ],
            };

            return new Padding
            {
                Amount = PaddingStyle.All(ShadowPadding - ResizeGrip / 2),
                Children =
                [
                    new BorderLayout
                    {
                        North = EdgeRow(WindowEdges.Top),
                        South = EdgeRow(WindowEdges.Bottom),
                        West = Grip(WindowEdges.Left, width: ResizeGrip),
                        East = Grip(WindowEdges.Right, width: ResizeGrip),
                    },
                ],
            };
        }
    }
}
