using GitBench.Infrastructure.Compat;
using ZGF.Gui;
using ZGF.Gui.Views;

namespace GitBench.Theming;

internal static class ThemedBindings
{
    extension(TextView view)
    {
        public void BindThemedTextColor(Func<ThemeStyles, uint> select) =>
            view.Behaviors.Add(new CompatThemedBehavior<ThemeStyles, uint>(select, c => view.TextColor = c));
    }

    extension(RectView view)
    {
        public void BindThemedBackgroundColor(Func<ThemeStyles, uint> select) =>
            view.Behaviors.Add(new CompatThemedBehavior<ThemeStyles, uint>(select, c => view.BackgroundColor = c));

        public void BindThemedBorderColor(Func<ThemeStyles, BorderColorStyle> select) =>
            view.Behaviors.Add(new CompatThemedBehavior<ThemeStyles, BorderColorStyle>(select, c => view.BorderColor = c));
    }

    extension(View view)
    {
        public void BindThemed(Action<ThemeStyles> onChange) =>
            view.Behaviors.Add(new CompatThemedBehavior<ThemeStyles, ThemeStyles>(s => s, onChange));
    }
}
