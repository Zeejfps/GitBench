using ZGF.Gui;
using ZGF.Gui.Desktop.Input;

namespace GitBench.Infrastructure.Compat;

/// <summary>
/// Mount-lifetime behavior that defers service resolution to attach time via
/// <see cref="ViewContexts"/>, mirroring the framework's pre-widget attach-to-context
/// semantics. The factory's returned disposable is disposed on detach.
/// </summary>
internal sealed class ContextScopedBehavior : IViewBehavior
{
    private readonly Func<View, Context, IDisposable?> _attach;
    private IDisposable? _resource;

    public ContextScopedBehavior(Func<View, Context, IDisposable?> attach)
    {
        _attach = attach;
    }

    public void Attach(View view)
    {
        _resource = _attach(view, ViewContexts.Require(view));
    }

    public void Detach(View view)
    {
        _resource?.Dispose();
        _resource = null;
    }
}

internal sealed class CompatControllerBehavior<T> : IViewBehavior where T : IKeyboardMouseController
{
    private readonly Func<Context, T>? _factory;
    private readonly EventPhaseFilter _phaseFilter;
    private readonly bool _ownsController;
    private T? _controller;
    private InputSystem? _input;

    public CompatControllerBehavior(Func<Context, T> factory, EventPhaseFilter phaseFilter = EventPhaseFilter.Both)
    {
        _factory = factory;
        _phaseFilter = phaseFilter;
        _ownsController = true;
    }

    public CompatControllerBehavior(T controller, EventPhaseFilter phaseFilter = EventPhaseFilter.Both)
    {
        _controller = controller;
        _phaseFilter = phaseFilter;
        _ownsController = false;
    }

    public void Attach(View view)
    {
        var ctx = ViewContexts.Require(view);
        _input = ctx.Require<InputSystem>();
        _controller ??= _factory!(ctx);
        _input.RegisterController(view, _controller, _phaseFilter);
    }

    public void Detach(View view)
    {
        if (_controller != null)
            _input?.UnregisterController(view, _controller);
        _input = null;

        if (_ownsController)
        {
            if (_controller is IDisposable disposable)
                disposable.Dispose();
            _controller = default;
        }
    }
}

internal sealed class ActionDisposable : IDisposable
{
    private Action? _dispose;

    public ActionDisposable(Action dispose)
    {
        _dispose = dispose;
    }

    public void Dispose()
    {
        _dispose?.Invoke();
        _dispose = null;
    }
}
