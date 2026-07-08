using GitBench.Controls;
using GitBench.Widgets;
using ZGF.Geometry;
using ZGF.Gui.Bindings;
using ZGF.Gui;
using ZGF.Gui.Desktop;
using ZGF.Gui.Desktop.Components.Controls;
using ZGF.Gui.Desktop.Components.ContextMenu;
using ZGF.Gui.Desktop.Components.TextInput;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.KeyboardModule;
using ZGF.Observable;

namespace GitBench.Features.Repos;

public readonly record struct MenuLabelSegment(string Text, uint? Color = null, bool Bold = false);

public static class RepoBarContextMenu
{
    public sealed record Item(
        string Label,
        Action OnSelected,
        string? Icon = null,
        bool Enabled = true,
        // Marks this item as the menu's current selection (e.g. the active identity or
        // language). Rendered with a tinted pill, a leading check, and bold accent text; the
        // whole menu then reserves the check column so unchecked rows align instead of jumping.
        bool Checked = false,
        IReadOnlyList<MenuLabelSegment>? LabelSegments = null,
        bool IsSeparator = false,
        string? Shortcut = null,
        IReadOnlyList<Item>? Submenu = null,
        // Lower bound on the width of the submenu this item opens, so a submenu of narrow rows
        // (e.g. single digits) stays wide enough to click comfortably. Ignored without a Submenu.
        float SubmenuMinWidth = 0f);

    public static readonly Item Separator = new(string.Empty, static () => { }, IsSeparator: true);

    /// <summary>
    /// Projects a <see cref="RowAction"/> into a menu item, deriving the shortcut hint from the
    /// action's gesture so the menu and the keyboard never disagree about a key.
    /// </summary>
    public static Item ToItem(RowAction action) => new(
        action.Label,
        action.Invoke,
        action.Icon,
        action.Enabled,
        Shortcut: action.Gesture?.Display);

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

    private const float SearchMenuMinWidth = 240f;
    private const float SearchMenuMaxListHeight = 320f;
    // The scrollbar gutter (matches VerticalScrollBarView.Width) folded into the menu width so the
    // bar sits beside the rows instead of over them.
    private const float SearchScrollbarGutter = 12f;

    /// <summary>
    /// Opens a context menu with a search box pinned to the top and a height-capped, scrollable item
    /// list — for menus with too many entries to pick from a flat stack (e.g. a branch picker). Typing
    /// filters the rows live, Enter commits the first match, Esc or an outside click dismisses it.
    /// Unlike <see cref="Show"/> it registers no keep-open controller, so moving the mouse off the
    /// menu (to type) doesn't close it.
    /// </summary>
    public static IOpenedContextMenu? ShowSearchable(
        Context context, PointF anchor, IReadOnlyList<Item> items,
        string searchPlaceholder, string noMatchesLabel, MenuPlacement placement = MenuPlacement.Below)
    {
        if (items.Count == 0) return null;
        var manager = context.Get<IContextMenuHost>();
        if (manager == null) return null;

        manager.CloseAllImmediately();

        var coords = context.Get<IWindowCoordinates>();
        var screen = coords != null ? coords.ToScreenPoints(anchor) : default;
        return manager.ShowContextMenu(
            popupCtx => BuildSearchableMenu(popupCtx, manager, items, searchPlaceholder, noMatchesLabel),
            screen, placement: placement);
    }

    private readonly record struct MenuRow(ContextMenuItem RowView, Action OnSelected, string Text);

    private static ContextMenu BuildSearchableMenu(
        Context ctx, IContextMenuHost manager, IReadOnlyList<Item> items,
        string searchPlaceholder, string noMatchesLabel)
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

        var itemsColumn = new ColumnView { Gap = Spacing.Hair };
        var rows = new List<MenuRow>();
        var separators = new List<View>();

        foreach (var item in items)
        {
            if (item.IsSeparator)
            {
                var sep = new RectView { Height = 1 };
                sep.BindThemedBackgroundColor(theme, s => s.ContextMenu.Border);
                itemsColumn.Children.Add(sep);
                separators.Add(sep);
                continue;
            }

            var menuItem = new ContextMenuItem(ctx.Canvas)
            {
                Text = item.Label,
                Icon = item.Icon,
                IconFontFamily = LucideIcons.FontFamily,
                NormalBackgroundColor = 0x00000000,
                IsEnabled = item.Enabled,
            };
            menuItem.BindThemed(theme, s =>
            {
                menuItem.SelectedBackgroundColor = s.ContextMenu.ItemSelectedBackground;
                menuItem.TextColor = s.ContextMenu.ItemText;
                menuItem.DisabledTextColor = s.ContextMenu.ItemTextDisabled;
            });
            var captured = item;
            menuItem.UseController(input, () => new ContextMenuItemDefaultKbmController(menuItem, ctx, () =>
                manager.CloseAllAndThen(captured.OnSelected)));
            itemsColumn.Children.Add(menuItem);
            rows.Add(new MenuRow(menuItem, captured.OnSelected, item.Label));
        }

        // Empty-state row, shown only while the filter excludes everything.
        var noMatches = new TextView(ctx.Canvas)
        {
            Text = noMatchesLabel,
            VerticalTextAlignment = TextAlignment.Center,
        };
        noMatches.BindThemedTextColor(theme, s => s.ContextMenu.ItemTextDisabled);
        var noMatchesRow = new PaddingView
        {
            Padding = PaddingStyle.All(Spacing.Sm),
            Children = { noMatches },
            IsVisible = false,
        };
        itemsColumn.Children.Add(noMatchesRow);

        // The width folds in the scrollbar gutter so rows aren't squeezed under the bar; the viewport
        // caps the list and lets it scroll. Both are measured once from the full list — the popup is a
        // fixed-size window, so filtering scrolls/shrinks within this viewport rather than resizing it.
        var menuWidth = MathF.Max(itemsColumn.MeasureWidth() + SearchScrollbarGutter, SearchMenuMinWidth);
        var viewportHeight = MathF.Min(itemsColumn.MeasureHeight(menuWidth), SearchMenuMaxListHeight);

        var scroll = new ScrollArea
        {
            Height = viewportHeight,
            AutoHide = true,
            Style = Theme.ScrollBar(),
            WheelStep = Scrolling.WheelStep,
            Children = [new Raw { View = itemsColumn }],
        }.BuildView(ctx);

        void Filter(string query)
        {
            var q = query.Trim();
            var active = q.Length > 0;
            var anyVisible = false;
            foreach (var row in rows)
            {
                var visible = !active || row.Text.Contains(q, StringComparison.OrdinalIgnoreCase);
                row.RowView.IsVisible = visible;
                anyVisible |= visible;
            }
            foreach (var sep in separators) sep.IsVisible = !active;
            noMatchesRow.IsVisible = !anyVisible;
        }

        void CommitFirst()
        {
            foreach (var row in rows)
                if (row.RowView.IsVisible)
                {
                    manager.CloseAllAndThen(row.OnSelected);
                    return;
                }
        }

        var search = BuildSearchField(ctx, searchPlaceholder, Filter, manager.RequestCloseAll, CommitFirst);

        var content = new ColumnView
        {
            Gap = Spacing.Xs,
            Width = menuWidth,
            Children = { search, scroll },
        };
        menu.SetContent(content);
        return menu;
    }

    // The menu's search field: a bordered input with a leading magnifier that grabs focus on mount,
    // filters on every edit, and routes Esc / Enter to the supplied callbacks.
    private static View BuildSearchField(
        Context ctx, string placeholder, Action<string> onQuery, Action onEscape, Action onCommit)
    {
        var theme = ctx.Theme();
        var input = ctx.Require<InputSystem>();
        var clipboard = ctx.Get<IClipboard>();

        var field = new TextInputView(ctx.Canvas)
        {
            FontSize = FontSize.Body,
            TextVerticalAlignment = TextAlignment.Center,
            PlaceholderText = placeholder,
            BackgroundColor = 0x00000000,
        };
        field.BindThemed(theme, s =>
        {
            field.TextColor = s.Palette.TextPrimary;
            field.PlaceholderTextColor = s.Palette.TextDisabled;
            field.CaretColor = s.Palette.TextPrimary;
            field.SelectionRectColor = s.Palette.Accent;
        });

        var controller = new SearchMenuInputController(field, input, clipboard, onEscape, onCommit);
        field.UseController(input, controller);
        field.Behaviors.Add(new SearchFocusOnMount(controller));
        field.Use(() => field.TextValue.Subscribe(onQuery));

        var icon = new TextView(ctx.Canvas)
        {
            Text = LucideIcons.Search,
            FontFamily = LucideIcons.FontFamily,
            FontSize = FontSize.Body,
            VerticalTextAlignment = TextAlignment.Center,
        };
        icon.BindThemedTextColor(theme, s => s.Palette.TextSecondary);

        var box = new RectView
        {
            BorderSize = BorderSizeStyle.All(1),
            BorderRadius = BorderRadiusStyle.All(Radius.Sm),
            Children =
            {
                new PaddingView
                {
                    Padding = PaddingStyle.All(Spacing.Sm),
                    Children =
                    {
                        new FlexRowView
                        {
                            Gap = Spacing.Sm,
                            CrossAxisAlignment = CrossAxisAlignment.Center,
                            Children =
                            {
                                icon,
                                new FlexItem { Grow = 1, Child = field },
                            },
                        },
                    },
                },
            },
        };
        box.BindThemed(theme, s =>
        {
            box.BackgroundColor = s.Palette.SurfaceSunken;
            box.BorderColor = BorderColorStyle.All(s.Palette.BorderSubtle);
        });
        return box;
    }

    // Search-box keyboard: Esc dismisses the menu, Enter commits the first match; everything else is
    // normal text editing (typing, caret, selection, clipboard).
    private sealed class SearchMenuInputController : BaseTextInputKbmController
    {
        private readonly Action _onEscape;
        private readonly Action _onCommit;

        public SearchMenuInputController(TextInputView view, InputSystem input, IClipboard? clipboard, Action onEscape, Action onCommit)
            : base(view, input, clipboard)
        {
            _onEscape = onEscape;
            _onCommit = onCommit;
        }

        protected override void OnKeyboardKeyPressed(ref KeyboardKeyEvent e)
        {
            switch (e.Key)
            {
                case KeyboardKey.Escape:
                    _onEscape();
                    e.Consume();
                    return;
                case KeyboardKey.Enter:
                case KeyboardKey.NumpadEnter:
                    _onCommit();
                    e.Consume();
                    return;
            }

            base.OnKeyboardKeyPressed(ref e);
        }
    }

    // Grabs the caret on mount so the search box is ready to type the moment the menu opens, and
    // releases it on unmount.
    private sealed class SearchFocusOnMount : IViewBehavior
    {
        private readonly BaseTextInputKbmController _controller;

        public SearchFocusOnMount(BaseTextInputKbmController controller) => _controller = controller;

        public void Attach(View view) => _controller.BeginEditing();

        public void Detach(View view) => _controller.EndEditing();
    }

    // Builds a themed menu from the item list. Recursed (via a per-item factory) for
    // submenus so nested menus share the same styling and click-to-close behavior.
    private static ContextMenu BuildMenu(Context ctx, IContextMenuHost manager, IReadOnlyList<Item> items, float minWidth = 0f)
    {
        var theme = ctx.Theme();
        var input = ctx.Require<InputSystem>();
        var menu = new ContextMenu
        {
            BorderSize = BorderSizeStyle.All(1),
            Padding = PaddingStyle.All(Spacing.Xs),
            MinWidth = minWidth,
        };
        menu.BindThemed(theme, s =>
        {
            menu.BackgroundColor = s.ContextMenu.Background;
            menu.BorderColor = BorderColorStyle.All(s.ContextMenu.Border);
        });

        // A menu that carries a current selection reserves the check column and rounds every
        // row's fill, so the active item's tinted pill and the plain rows share one column.
        var hasSelection = false;
        foreach (var item in items)
            if (!item.IsSeparator && item.Checked) { hasSelection = true; break; }

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
                Icon = item.Checked ? LucideIcons.Check : item.Icon,
                IconFontFamily = LucideIcons.FontFamily,
                NormalBackgroundColor = 0x00000000,
                IsEnabled = item.Enabled,
                Shortcut = item.Shortcut,
                ReserveIconColumn = hasSelection,
                BackgroundCornerRadius = hasSelection ? BorderRadiusStyle.All(Radius.Sm) : default,
            };
            var isChecked = item.Checked;
            menuItem.BindThemed(theme, s =>
            {
                menuItem.SelectedBackgroundColor = s.ContextMenu.ItemSelectedBackground;
                menuItem.NormalBackgroundColor = isChecked ? s.ContextMenu.ItemActiveBackground : 0x00000000;
                menuItem.TextColor = s.ContextMenu.ItemText;
                menuItem.DisabledTextColor = s.ContextMenu.ItemTextDisabled;
                menuItem.ShortcutColor = s.ContextMenu.ItemTextDisabled;
            });

            // The active row's label reads bold in the accent text color; explicit segments
            // still win when a caller supplies them.
            if (item.LabelSegments is { Count: > 0 } segs)
                menuItem.SetLabelView(BuildSegmentsView(ctx, segs, item.Enabled));
            else if (isChecked)
                menuItem.SetLabelView(BuildSegmentsView(ctx, [new MenuLabelSegment(item.Label, Bold: true)], item.Enabled));

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
                    subMenuFactory: subCtx => BuildMenu(subCtx, manager, submenu, captured.SubmenuMinWidth)));
            }
            else
            {
                menuItem.UseController(input, () => new ContextMenuItemDefaultKbmController(menuItem, ctx, () =>
                    // Dismiss the entire menu chain (parent + any open submenus), then act — the
                    // action runs only once the popups are actually hidden, so a blocking picker
                    // never opens behind a still-visible menu. Deferred close: a synchronous
                    // teardown here would unregister controllers mid-dispatch and mutate the input
                    // system's focus queue while it iterates.
                    manager.CloseAllAndThen(captured.OnSelected)));
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
