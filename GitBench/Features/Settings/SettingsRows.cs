using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Settings;

/// <summary>A settings section's caption.</summary>
internal sealed record SettingsSectionHeader : Widget
{
    public required Prop<string?> Value { get; init; }

    protected override IWidget Build(Context ctx) => new Text
    {
        Value = Value,
        FontSize = FontSize.Caption,
        Weight = FontWeight.Bold,
        Color = Theme.Color(s => s.DialogBody.SectionHeaderText),
    };
}

/// <summary>One setting: its name and a line about it on the leading side, the control that
/// changes it on the trailing side.</summary>
internal sealed record SettingsRow : Widget
{
    public required Prop<string?> Label { get; init; }
    public required Prop<string?> Description { get; init; }
    public required IWidget Control { get; init; }

    /// <summary>An id for the description text, for a row whose description is live status a test
    /// reads back.</summary>
    public string? DescriptionId { get; init; }

    protected override IWidget Build(Context ctx) => new Row
    {
        Gap = Spacing.Lg,
        CrossAxis = CrossAxisAlignment.Center,
        Children =
        [
            new Grow
            {
                Child = new Column
                {
                    Gap = Spacing.Xs,
                    CrossAxis = CrossAxisAlignment.Stretch,
                    Children =
                    [
                        new Text
                        {
                            Value = Label,
                            Color = Theme.Color(s => s.DialogBody.RowText),
                        },
                        new Text
                        {
                            Id = DescriptionId,
                            Value = Description,
                            Wrap = TextWrap.Wrap,
                            FontSize = FontSize.Caption,
                            Color = Theme.Color(s => s.Palette.TextMuted),
                        },
                    ],
                },
            },
            Control,
        ],
    };
}
