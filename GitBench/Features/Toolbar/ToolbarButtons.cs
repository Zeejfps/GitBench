using GitBench.Controls;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Toolbar;

/// <summary>An icon + label toolbar button wired for keyboard/mouse activation.</summary>
internal sealed record ToolbarButton : Widget
{
    public ICommand? Command { get; init; }
    public required Prop<string?> Icon { get; init; }
    public required Prop<string?> Label { get; init; }

    protected override IWidget Build(Context ctx) => new ButtonWidget
    {
        Command = Command,
        Children =
        [
            new ButtonIcon { Value = Icon },
            new ButtonLabel { Value = Label },
        ],
    }.WithController<KbmController>();
}

/// <summary>A compact icon-only toolbar button with a tooltip.</summary>
internal sealed record ToolbarIconButton : Widget
{
    public ICommand? Command { get; init; }
    public required Prop<string?> Icon { get; init; }
    public Prop<string?> Tooltip { get; init; }

    protected override IWidget Build(Context ctx) => new ButtonWidget
    {
        Command = Command,
        ContentInset = ButtonStyle.Plain.IconOnlyInset,
        Children = [new ButtonIcon { Value = Icon }],
    }.WithTooltip(Tooltip).WithController<KbmController>();
}

/// <summary>
/// A remote-sync toolbar button (fetch / pull / push): the icon swaps to a spinning loader while
/// the op runs, an optional ahead/behind count badge hugs the glyph, and the label switches to its
/// busy text.
/// </summary>
internal sealed record ToolbarSyncButton : Widget
{
    public required ICommand Command { get; init; }
    public required IReadable<bool> IsBusy { get; init; }
    public required string Icon { get; init; }
    public required IReadable<float> Rotation { get; init; }
    public required Prop<string?> Label { get; init; }
    public IReadable<int?>? Badge { get; init; }
    public Func<ThemeStyles, uint>? BadgeAccent { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var icon = new ButtonIcon
        {
            Value = IsBusy.Bind(string? (b) => b ? LucideIcons.Loader : Icon),
            Rotation = Prop.Bind(Rotation),
        };
        if (Badge is { } badge && BadgeAccent is { } accent)
            icon = icon with
            {
                Badge = Prop.Bind(badge),
                BadgeColor = Theme.Color(accent),
                GlyphColor = TintWhenPending(badge, accent),
            };

        return new ButtonWidget
        {
            Command = Command,
            Children = [icon, new ButtonLabel { Value = Label }],
        }.WithController<KbmController>();
    }

    // With commits pending the glyph takes its badge color, so arrow and count read as one unit;
    // with nothing pending it falls back to the ambient foreground.
    private static Prop<uint> TintWhenPending(IReadable<int?> badge, Func<ThemeStyles, uint> accent) =>
        Prop.Deferred<uint>(ctx =>
        {
            var tint = Theme.Color(accent).ToReadable(ctx);
            var foreground = Foreground.Color.ToReadable(ctx);
            return Prop.Bind(() => badge.Value is > 0 ? tint.Value : foreground.Value);
        });
}
