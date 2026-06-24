using GitBench.Controls;
using GitBench.Widgets;
using ZGF.Geometry;
using ZGF.Gui.Bindings;
using ZGF.Gui;
using ZGF.Gui.Desktop;
using ZGF.Gui.Desktop.Components.ContextMenu;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Views;

namespace GitBench.Features.Repos;

public readonly record struct MenuLabelSegment(string Text, uint? Color = null, bool Bold = false);

public static class RepoBarContextMenu
{
    public sealed record Item(
        string Label,
        Action OnSelected,
        string? Icon = null,
        bool Enabled = true,
        IReadOnlyList<MenuLabelSegment>? LabelSegments = null,
        bool IsSeparator = false,
        string? Shortcut = null,
        IReadOnlyList<Item>? Submenu = null);

    public static readonly Item Separator = new(string.Empty, static () => { }, IsSeparator: true);

    public static IOpenedContextMenu? Show(Context context, PointF anchor, IReadOnlyList<Item> items, MenuPlacement placement = MenuPlacement.Below)
    {
        if (items.Count == 0) return null;
        var manager = context.Get<IContextMenuHost>();
        if (manager == null) return null;

        manager.CloseAllImmediately();

        var coords = context.Get<IWindowCoordinates>();
        var screen = coords != null ? coords.ToScreenPoints(anchor) : default;
        var opened = manager.ShowContextMenu(
            popupCtx => BuildMenu(popupCtx, manager, items),
            screen, placement: placement);
        if (opened == null) return null;

        // Keyboard navigation registers against the popup's own input system; the popup's
        // teardown (close) drops the registration with it.
        opened.Context.Get<InputSystem>()?.RegisterController(opened.Menu, new ContextMenuKbmController(opened));
        return opened;
    }

    // Builds a themed menu from the item list. Recursed (via a per-item factory) for
    // submenus so nested menus share the same styling and click-to-close behavior.
    private static ContextMenu BuildMenu(Context ctx, IContextMenuHost manager, IReadOnlyList<Item> items)
    {
        var theme = ctx.Theme();
        var input = ctx.Require<InputSystem>();
        var menu = new ContextMenu
        {
            BorderSize = BorderSizeStyle.All(1),
            Padding = PaddingStyle.All(Spacing.Xs),
        };
        menu.BindThemed(theme, s =>
        {
            menu.BackgroundColor = s.ContextMenu.Background;
            menu.BorderColor = BorderColorStyle.All(s.ContextMenu.Border);
        });

        foreach (var item in items)
        {
            if (item.IsSeparator)
            {
                var separator = new RectView { Height = 1 };
                separator.BindThemedBackgroundColor(theme, s => s.ContextMenu.Border);
                menu.Children.Add(separator);
                continue;
            }

            var menuItem = new ContextMenuItem(ctx.Canvas)
            {
                Text = item.Label,
                Icon = item.Icon,
                IconFontFamily = LucideIcons.FontFamily,
                NormalBackgroundColor = 0x00000000,
                IsEnabled = item.Enabled,
                Shortcut = item.Shortcut,
            };
            menuItem.BindThemed(theme, s =>
            {
                menuItem.SelectedBackgroundColor = s.ContextMenu.ItemSelectedBackground;
                menuItem.TextColor = s.ContextMenu.ItemText;
                menuItem.DisabledTextColor = s.ContextMenu.ItemTextDisabled;
                menuItem.ShortcutColor = s.ContextMenu.ItemTextDisabled;
            });

            if (item.LabelSegments is { Count: > 0 } segs)
                menuItem.SetLabelView(BuildSegmentsView(ctx, segs, item.Enabled));

            var captured = item;
            if (captured.Submenu is { Count: > 0 } submenu)
            {
                // Parent items open their submenu on hover and have no click action. The
                // trailing chevron is drawn as a Lucide glyph (set before the controller
                // flips IsArrowVisible on).
                menuItem.ArrowGlyph = LucideIcons.ChevronRight;
                menuItem.ArrowFontFamily = LucideIcons.FontFamily;
                menuItem.UseController(input, () => new ContextMenuItemDefaultKbmController(
                    menuItem, ctx,
                    subMenuFactory: subCtx => BuildMenu(subCtx, manager, submenu)));
            }
            else
            {
                menuItem.UseController(input, () => new ContextMenuItemDefaultKbmController(menuItem, ctx, () =>
                {
                    // Dismiss the entire menu chain (parent + any open submenus), then act.
                    // Deferred close — a synchronous teardown here would unregister controllers
                    // mid-dispatch and mutate the input system's focus queue while it iterates.
                    manager.RequestCloseAll();
                    captured.OnSelected();
                }));
            }
            menu.Children.Add(menuItem);
        }

        return menu;
    }

    private static View BuildSegmentsView(Context ctx, IReadOnlyList<MenuLabelSegment> segments, bool enabled)
    {
        var theme = ctx.Theme();
        var row = new FlexRowView
        {
            Gap = Spacing.None,
            CrossAxisAlignment = CrossAxisAlignment.Center,
        };
        foreach (var seg in segments)
        {
            var tv = new TextView(ctx.Canvas)
            {
                Text = seg.Text,
                VerticalTextAlignment = TextAlignment.Center,
            };
            if (seg.Color.HasValue && enabled)
            {
                tv.TextColor = seg.Color.Value;
            }
            else if (seg.Bold && enabled)
            {
                tv.BindThemedTextColor(theme, s => s.ContextMenu.AccentText);
            }
            else
            {
                tv.BindThemedTextColor(theme, s => enabled ? s.ContextMenu.ItemText : s.ContextMenu.ItemTextDisabled);
            }
            if (seg.Bold && enabled)
                tv.FontWeight = FontWeight.Bold;
            row.Children.Add(tv);
        }
        return row;
    }
}
