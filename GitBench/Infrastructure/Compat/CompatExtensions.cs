using ZGF.Gui;
using ZGF.Gui.Desktop.Input;
using ZGF.Observable;

namespace GitBench.Infrastructure.Compat;

/// <summary>
/// Pre-widget framework signatures, re-implemented over <see cref="ViewContexts"/> deferred
/// resolution so existing call sites compile unchanged. Each shim mirrors the semantics the
/// framework had before the widget refactor (resolve on mount, dispose on unmount).
/// </summary>
public static class CompatExtensions
{
    extension(View view)
    {
        /// <summary>The context of the window this view is mounted under, if registered.</summary>
        public Context? Context => ViewContexts.Find(view);

        public void Use<T>(Func<Context, T?> factory) where T : class, IDisposable
        {
            view.Behaviors.Add(new ContextScopedBehavior((_, ctx) => factory(ctx)));
        }

        public void UseViewModel<TVm>(Func<Context, TVm> factory, Action<TVm> bind)
            where TVm : IDisposable
        {
            view.Behaviors.Add(new ContextScopedBehavior((_, ctx) =>
            {
                var vm = factory(ctx);
                bind(vm);
                return vm;
            }));
        }

        public void UseViewModel<TVm>(Action<TVm> bind)
            where TVm : class, IDisposable
        {
            view.Behaviors.Add(new ContextScopedBehavior((_, ctx) =>
            {
                var vm = ctx.Require<TVm>();
                bind(vm);
                return vm;
            }));
        }

        public void UseViewModel<TVm>(IBind<TVm> target)
            where TVm : class, IDisposable
        {
            view.Behaviors.Add(new ContextScopedBehavior((_, ctx) =>
            {
                var vm = ctx.Require<TVm>();
                target.Bind(vm);
                return vm;
            }));
        }

        public void UseController<T>(Func<Context, T> factory)
            where T : IKeyboardMouseController
        {
            view.Behaviors.Add(new CompatControllerBehavior<T>(factory));
        }

        public void UseController<T>(Func<Context, T> factory, EventPhaseFilter phaseFilter)
            where T : IKeyboardMouseController
        {
            view.Behaviors.Add(new CompatControllerBehavior<T>(factory, phaseFilter));
        }

        public void UseController<T>(T controller)
            where T : IKeyboardMouseController
        {
            view.Behaviors.Add(new CompatControllerBehavior<T>(controller));
        }

        public void UseController<T>(T controller, EventPhaseFilter phaseFilter)
            where T : IKeyboardMouseController
        {
            view.Behaviors.Add(new CompatControllerBehavior<T>(controller, phaseFilter));
        }
    }

}

/// <summary>
/// Theme-selected property binding that resolves the theme service on mount (the framework's
/// equivalent binds a theme instance captured at build time).
/// </summary>
internal sealed class CompatThemedBehavior<TStyles, TProp> : IViewBehavior
{
    private readonly Func<TStyles, TProp> _select;
    private readonly Action<TProp> _apply;
    private Derived<TProp>? _derived;
    private IDisposable? _subscription;

    public CompatThemedBehavior(Func<TStyles, TProp> select, Action<TProp> apply)
    {
        _select = select;
        _apply = apply;
    }

    public void Attach(View view)
    {
        var theme = ViewContexts.Require(view).Require<IThemeService<TStyles>>();
        _derived = new Derived<TProp>(() => _select(theme.Styles.Value));
        _subscription = _derived.Subscribe(_apply);
    }

    public void Detach(View view)
    {
        _subscription?.Dispose();
        _subscription = null;
        _derived?.Dispose();
        _derived = null;
    }
}
