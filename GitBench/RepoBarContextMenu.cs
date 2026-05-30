using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;

namespace GitGui;

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
        string? Shortcut = null);

    public static readonly Item Separator = new(string.Empty, static () => { }, IsSeparator: true);

    public static IOpenedContextMenu? Show(Context context, PointF anchor, IReadOnlyList<Item> items)
    {
        if (items.Count == 0) return null;
        var manager = context.Get<ContextMenuManager>();
        if (manager == null) return null;

        manager.CloseAllImmediately();

        var menu = new ContextMenu
        {
            BorderSize = BorderSizeStyle.All(1),
            Padding = PaddingStyle.All(4),
        };
        menu.BindThemed(s =>
        {
            menu.BackgroundColor = s.ContextMenu.Background;
            menu.BorderColor = BorderColorStyle.All(s.ContextMenu.Border);
        });

        foreach (var item in items)
        {
            if (item.IsSeparator)
            {
                var separator = new RectView { Height = 1 };
                separator.BindThemedBackgroundColor(s => s.ContextMenu.Border);
                menu.Children.Add(separator);
                continue;
            }

            var menuItem = new ContextMenuItem
            {
                Text = item.Label,
                Icon = item.Icon,
                IconFontFamily = LucideIcons.FontFamily,
                NormalBackgroundColor = 0x00000000,
                IsEnabled = item.Enabled,
                Shortcut = item.Shortcut,
            };
            menuItem.BindThemed(s =>
            {
                menuItem.SelectedBackgroundColor = s.ContextMenu.ItemSelectedBackground;
                menuItem.TextColor = s.ContextMenu.ItemText;
                menuItem.DisabledTextColor = s.ContextMenu.ItemTextDisabled;
                menuItem.ShortcutColor = s.ContextMenu.ItemTextDisabled;
            });

            if (item.LabelSegments is { Count: > 0 } segs)
                menuItem.SetLabelView(BuildSegmentsView(segs, item.Enabled));

            var captured = item;
            menuItem.UseController(ctx => new ContextMenuItemDefaultKbmController(menuItem, ctx, () =>
            {
                manager.RequestCloseMenu(menu);
                captured.OnSelected();
            }));
            menu.Children.Add(menuItem);
        }

        var coords = context.Get<IWindowCoordinates>();
        var screen = coords != null ? coords.ToScreenPoints(anchor) : default;
        var opened = manager.ShowContextMenu(menu, screen);
        if (opened == null) return null;

        menu.UseController(_ => new ContextMenuKbmController(opened));
        return opened;
    }

    private static MultiChildView BuildSegmentsView(IReadOnlyList<MenuLabelSegment> segments, bool enabled)
    {
        var row = new FlexRowView
        {
            Gap = 0,
            CrossAxisAlignment = CrossAxisAlignment.Center,
        };
        foreach (var seg in segments)
        {
            var tv = new TextView
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
                tv.BindThemedTextColor(s => s.ContextMenu.AccentText);
            }
            else
            {
                tv.BindThemedTextColor(s => enabled ? s.ContextMenu.ItemText : s.ContextMenu.ItemTextDisabled);
            }
            if (seg.Bold && enabled)
                tv.FontWeight = FontWeight.Bold;
            row.Children.Add(tv);
        }
        return row;
    }
}
