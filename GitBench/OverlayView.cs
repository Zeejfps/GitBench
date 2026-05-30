using ZGF.Gui;
using ZGF.Gui.Desktop;
using ZGF.Gui.Views;

namespace GitGui;

public sealed class OverlayView : MultiChildView
{
    private IMessageBus? _messageBus;

    private readonly RectView _background;
    private View? _currentDialog;
    private float _currentDialogWidth;
    private float _currentDialogHeight;
    private bool _isOpen;

    private OverlayInputHandler _inputHandler;
    
    public OverlayView()
    {
        ZIndex = 1000;
        _background = new RectView
        {
            BackgroundColor = 0xB0000000,
        };
        Children.Add(_background);
        // _inputHandler = new OverlayInputHandler();
    }

    // protected override void OnAttachedToContext(Context context)
    // {
    //     _messageBus = context.Get<IMessageBus>();
    //     _messageBus?.Subscribe<AddRepoMessage>(OnAddRepoMessageReceived);
    //     _messageBus?.Subscribe<ShowCheckoutDialogMessage>(OnShowCheckoutDialog);
    //     _messageBus?.Subscribe<ShowCheckoutErrorMessage>(OnShowCheckoutError);
    // }
    //
    // protected override void OnDetachedFromContext(Context context)
    // {
    //     _messageBus?.Unsubscribe<AddRepoMessage>(OnAddRepoMessageReceived);
    //     _messageBus?.Unsubscribe<ShowCheckoutDialogMessage>(OnShowCheckoutDialog);
    //     _messageBus?.Unsubscribe<ShowCheckoutErrorMessage>(OnShowCheckoutError);
    //     _messageBus = null;
    // }
    //
    // private void OnAddRepoMessageReceived(AddRepoMessage _)
    //     => ShowDialog(new AddRepoDialog(Close), 360f, 230f);
    //
    // private void OnShowCheckoutDialog(ShowCheckoutDialogMessage m)
    //     => ShowDialog(
    //         new CheckoutBranchDialog(m.Repo, m.RemoteName, m.RemoteBranchName, m.SuggestedLocalName, Close),
    //         420f, 280f);
    //
    // private void OnShowCheckoutError(ShowCheckoutErrorMessage m)
    //     => ShowDialog(new CheckoutErrorDialog(m.Message, Close), 460f, 220f);
    //
    // private void ShowDialog(View dialog, float width, float height)
    // {
    //     if (_currentDialog != null) Children.Remove(_currentDialog);
    //
    //     _currentDialog = dialog;
    //     _currentDialogWidth = width;
    //     _currentDialogHeight = height;
    //     _background.BackgroundColor = 0xB0000000;
    //     _isOpen = true;
    //     //Behaviors.Add(_inputHandler);
    //     Children.Add(_currentDialog);
    // }
    //
    // private void Close()
    // {
    //     if (!_isOpen) return;
    //     if (_currentDialog != null) Children.Remove(_currentDialog);
    //     _currentDialog = null;
    //     _background.BackgroundColor = 0;
    //     _isOpen = false;
    //     Behaviors.Remove(_inputHandler);
    // }

    // protected override void OnLayoutChildren()
    // {
    //     var position = Position;
    //     _background.LeftConstraint = position.Left;
    //     _background.BottomConstraint = position.Bottom;
    //     _background.MinWidthConstraint = position.Width;
    //     _background.MaxWidthConstraint = position.Width;
    //     _background.MaxHeightConstraint = position.Height;
    //     _background.LayoutSelf();
    //
    //     if (!_isOpen || _currentDialog == null) return;
    //
    //     var w = _currentDialogWidth;
    //     var h = _currentDialogHeight;
    //     _currentDialog.LeftConstraint = position.Left + (position.Width - w) * 0.5f;
    //     _currentDialog.BottomConstraint = position.Bottom + (position.Height - h) * 0.5f;
    //     _currentDialog.MinWidthConstraint = w;
    //     _currentDialog.MaxWidthConstraint = w;
    //     _currentDialog.MaxHeightConstraint = h;
    //     _currentDialog.LayoutSelf();
    // }
}

public sealed class OverlayInputHandler : KeyboardMouseController
{
    public override void OnMouseEnter(ref MouseEnterEvent e)
    {
        //e.Consume();
    }
}
