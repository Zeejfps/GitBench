using System.Runtime.CompilerServices;
using ZGF.Gui;

namespace GitBench.Infrastructure.Compat;

/// <summary>
/// Transitional bridge to the framework's build-time-injection model: maps each window's
/// root view to the Context it was built with, so legacy views (built without a context)
/// can still resolve services when they mount. Every window root must be registered at
/// build time (main window in Program.cs, secondary windows / popups / menus at their
/// BuildRoot factories). Remove once views are migrated to widgets.
/// </summary>
public static class ViewContexts
{
    private static readonly ConditionalWeakTable<View, Context> Roots = new();

    public static T RegisterRoot<T>(T root, Context context) where T : View
    {
        Roots.AddOrUpdate(root, context);
        return root;
    }

    public static Context? Find(View view)
    {
        for (var v = view; v != null; v = v.Parent)
        {
            if (Roots.TryGetValue(v, out var ctx))
                return ctx;
        }
        return null;
    }

    public static Context Require(View view) =>
        Find(view) ?? throw new InvalidOperationException(
            $"No context registered for the tree containing {view.GetType().Name}. " +
            "Register the window root via ViewContexts.RegisterRoot in its BuildRoot factory.");
}
