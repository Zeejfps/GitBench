using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;

namespace GitGui;

internal static class ThemedBindings
{
    extension(TextView view)
    {
        public void BindThemedTextColor(Func<ThemeStyles, uint> select) =>
            view.BindThemedTextColor<ThemeStyles>(select);
    }

    extension(RectView view)
    {
        public void BindThemedBackgroundColor(Func<ThemeStyles, uint> select) =>
            view.BindThemedBackgroundColor<ThemeStyles>(select);

        public void BindThemedBorderColor(Func<ThemeStyles, BorderColorStyle> select) =>
            view.BindThemedBorderColor<ThemeStyles>(select);
    }

    extension(View view)
    {
        public void BindThemed(Action<ThemeStyles> onChange) =>
            view.BindThemed<ThemeStyles>(onChange);
    }
}
