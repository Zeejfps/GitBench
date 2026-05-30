using ZGF.Gui;
using ZGF.Gui.Desktop;
using ZGF.Observable;

namespace GitGui;

public abstract class HoverableButton : MultiChildView
{
    private readonly Action? _onClick;
    private readonly HoverableButtonController _controller;

    protected State<bool> IsHovered { get; } = new(false);
    public State<bool> IsEnabled { get; } = new(true);

    // Set when the button is the active focus-ring stop, so subclasses can render the
    // same chrome they use for pointer hover.
    public State<bool> IsFocusHighlighted { get; } = new(false);
    public State<ICommand?> Command { get; } = new(null);

    // Focus-traversal hooks, forwarded to the controller; wired by the owner's focus ring.
    public Action? OnTab
    {
        get => _controller.OnTab;
        set => _controller.OnTab = value;
    }

    public Action? OnShiftTab
    {
        get => _controller.OnShiftTab;
        set => _controller.OnShiftTab = value;
    }

    protected HoverableButton(Action? onClick = null, string? tooltip = null)
    {
        _onClick = onClick;
        _controller = new HoverableButtonController(
            () => { if (IsEnabled) OnClicked(); },
            h => IsHovered.Set(h),
            f => IsFocusHighlighted.Set(f));
        this.UseController(_ => _controller);

        if (!string.IsNullOrEmpty(tooltip))
        {
            this.UsePresenter(ctx => new Tooltip(this, ctx, tooltip, IsHovered, IsEnabled));
        }
    }

    // Steals keyboard focus to this button (highlighting it); paired with Blur for the
    // focus ring. No-op until the button is attached to a context.
    public void FocusSelf()
    {
        var input = Context?.Get<InputSystem>();
        Console.WriteLine($"[focusdbg] FocusSelf {GetType().Name} ctx={Context != null} input={input != null} interactable={(input?.IsInteractable(_controller))}");
        input?.StealFocus(_controller);
    }
    public void Blur() => Context?.Get<InputSystem>()?.Blur(_controller);

    protected virtual void OnClicked()
    {
        if (Command.Value is { } cmd) cmd.Execute();
        else _onClick?.Invoke();
    }

    public void BindCommand(ICommand command)
    {
        Command.Value = command;
        IsEnabled.BindTo(command.CanExecute);
    }

    /// <summary>
    /// Drives <see cref="IsEnabled"/> as the inverse of <paramref name="busy"/>. For secondary
    /// buttons (typically Cancel) that should lock out while a sibling <see cref="AsyncCommand"/>
    /// is mid-flight, pass that command's <c>IsRunning</c>.
    /// </summary>
    public void DisableWhile(IReadable<bool> busy)
        => IsEnabled.BindTo(new Derived<bool>(() => !busy.Value));

    protected void SetBackground(View background) => AddChildToSelf(background);
}
