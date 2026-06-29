using GitBench.Features.Commits;
using GitBench.Features.LocalChanges;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Controls.Dialogs;

/// <summary>
/// A selectable file row for the Discard / Stash dialogs: a status badge and path sitting behind a
/// checkbox, with the whole row acting as one multi-select target. A checked row reads as selected —
/// the row takes a neutral surface highlight, leaving the accent checkbox as the sole colour cue (the
/// shared accent-tinted selection fill would clash with the checkbox) — and a click reports the held
/// modifiers so the dialog can toggle a single row or extend a range (Shift). The checkbox is
/// display-only; the row owns the input.
/// </summary>
internal static class DialogFileRow
{
    public static View Build(
        Context ctx,
        FileChange display,
        string path,
        IReadable<IReadOnlySet<string>> checkedPaths,
        Action<InputModifiers> onClick)
    {
        var theme = ctx.Theme();
        var input = ctx.Require<InputSystem>();

        var pathText = new TextView(ctx.Canvas)
        {
            Text = FileChangeFormatting.FormatPath(display),
            VerticalTextAlignment = TextAlignment.Center,
        };
        pathText.BindTextColor(() => theme.Styles.Value.FileChangeRow.RowText);

        var rowContent = new FlexRowView
        {
            Gap = Spacing.Md,
            CrossAxisAlignment = CrossAxisAlignment.Center,
            Children =
            {
                new FileStatusBadge { Status = display.Status }.BuildView(ctx),
                new FlexItem { Grow = 1, Child = pathText },
            },
        };

        // Give the checkbox a little breathing room from the row's leading edge.
        var inset = new Padding
        {
            Amount = new PaddingStyle { Left = Spacing.Md },
            Children =
            [
                new CheckboxWidget
                {
                    Checked = Prop.Bind(() => checkedPaths.Value.Contains(path)),
                    Content = new Raw { View = rowContent },
                    Height = Sizes.RowHeight,
                },
            ],
        }.BuildView(ctx);

        var row = new RowView(theme, checkedPaths, path);
        row.Children.Add(inset);
        row.UseController(input, () => new RowController(row, onClick));
        return row;
    }

    // Draws a full-bleed neutral selected/hover highlight behind its content; the row's height comes
    // from the checkbox it wraps. Repaints whenever the checked set, theme, or hover state changes.
    private sealed class RowView : View
    {
        private readonly IThemeService<ThemeStyles> _theme;
        private readonly Func<bool> _isSelected;

        public RowView(
            IThemeService<ThemeStyles> theme,
            IReadable<IReadOnlySet<string>> checkedPaths,
            string path)
        {
            _theme = theme;
            _isSelected = () => checkedPaths.Value.Contains(path);
            this.Bind(checkedPaths, _ => Repaint());
            this.Bind(theme.Styles, _ => Repaint());
            this.Bind(Hovered, _ => Repaint());
        }

        public new ChildrenCollection Children => base.Children;

        public State<bool> Hovered { get; } = new(false);

        private void Repaint() => SetDirty();

        protected override void OnDrawSelf(ICanvas c)
        {
            var palette = _theme.Styles.Value.Palette;
            var fill = _isSelected()
                ? palette.SurfaceHoverStrong
                : Hovered.Value ? palette.SurfaceHover : 0u;
            if (fill == 0u) return;

            c.DrawRect(new DrawRectInputs
            {
                Position = Position,
                Style = new RectStyle { BackgroundColor = fill },
                ZIndex = GetDrawZIndex(),
            });
        }
    }

    private sealed class RowController(RowView row, Action<InputModifiers> onClick) : KeyboardMouseController
    {
        public override void OnMouseEnter(ref MouseEnterEvent e) => row.Hovered.Value = true;

        public override void OnMouseExit(ref MouseExitEvent e) => row.Hovered.Value = false;

        public override void OnMouseButtonStateChanged(ref MouseButtonEvent e)
        {
            if (e.Phase != EventPhase.Bubbling) return;
            if (e.Button != MouseButton.Left || e.State != InputState.Pressed) return;
            onClick(e.Modifiers);
            e.Consume();
        }
    }
}
