using GitBench.Controls.Dialogs;
using GitBench.Features.Repos;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Controls;

internal sealed record OptionDropdown<T> : Widget where T : struct, Enum
{
    public required State<T> Selected { get; init; }
    public required (T Value, string Label, string Detail)[] Options { get; init; }
    public Func<T, uint>? DotColor { get; init; }

    protected override IWidget Build(Context ctx)
    {
        (string Label, string Detail) Lookup(T value)
        {
            foreach (var o in Options) if (o.Value.Equals(value)) return (o.Label, o.Detail);
            return (string.Empty, string.Empty);
        }

        List<RepoBarContextMenu.Item> BuildItems()
        {
            var items = new List<RepoBarContextMenu.Item>(Options.Length);
            foreach (var (value, label, detail) in Options)
            {
                var picked = value;
                items.Add(new RepoBarContextMenu.Item(
                    $"{label} — {detail}",
                    () => Selected.Value = picked,
                    LabelSegments: DotColor is { } dot
                        ? [new MenuLabelSegment("● ", dot(value)), new MenuLabelSegment(label, Bold: true), new MenuLabelSegment("  " + detail)]
                        : null));
            }
            return items;
        }

        var content = new List<IWidget>(3);
        if (DotColor is { } dotColor)
        {
            content.Add(new Text
            {
                Value = "●",
                FontSize = FontSize.Body,
                Width = 14,
                HAlign = TextAlignment.Center,
                VAlign = TextAlignment.Center,
                Color = Prop.Bind(() => dotColor(Selected.Value)),
            });
        }
        content.Add(new Text
        {
            VAlign = TextAlignment.Center,
            Value = Prop.Bind<string?>(() => Lookup(Selected.Value).Label),
            Color = Theme.Color(t => t.DialogFrame.TitleText),
        });
        content.Add(new Grow
        {
            Child = new Text
            {
                VAlign = TextAlignment.Center,
                Wrap = TextWrap.NoWrap,
                Overflow = TextOverflow.Ellipsis,
                Value = Prop.Bind<string?>(() => Lookup(Selected.Value).Detail),
                Color = Theme.Color(t => t.DialogBody.RowTextMissing),
            },
        });

        return new DropdownWidget
        {
            Height = DialogFrame.FieldHeight,
            Gap = Spacing.Md,
            Children = content.ToArray(),
        }.WithMenuController(rect => RepoBarContextMenu.Show(ctx, rect.BottomLeft, BuildItems()));
    }
}
