using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Views;

namespace GitBench.Controls.Dialogs;

public sealed class DialogSurfaceView : ContainerView
{
    private readonly ContainerView _overlay;

    public DialogSurfaceView()
    {
        _overlay = new ContainerView
        {
            ZIndex = 1000,
        };
    }

    public void ShowDialog(View dialog)
    {
        var backdrop = new RectView
        {
            BackgroundColor = 0xB0000000,
        };
        backdrop.UseController(_ => new DialogInputBlockingController());
        _overlay.Children.Add(backdrop);

        _overlay.Children.Add(new CenterView
        {
            Children =
            {
                dialog,
            }
        });

        Children.Add(_overlay);
    }

    public void HideDialog()
    {
        _overlay.Children.Clear();
        Children.Remove(_overlay);
    }
}

internal sealed class DialogInputBlockingController : KeyboardMouseController
{
    public override void OnMouseButtonStateChanged(ref MouseButtonEvent e)
    {
        e.Consume();
    }

    public override void OnMouseWheelScrolled(ref MouseWheelScrolledEvent e)
    {
        e.Consume();
    }

    public override void OnMouseMoved(ref MouseMoveEvent e)
    {
        e.Consume();
    }
}
