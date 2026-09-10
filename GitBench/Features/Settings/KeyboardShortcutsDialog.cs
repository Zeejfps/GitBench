using GitBench.Controls.Dialogs;
using GitBench.Input;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Settings;

/// <summary>The key map as a scrolling reference: every command grouped by surface, with its caps.</summary>
internal sealed record KeyboardShortcutsDialog : Widget<DialogState>
{
    private const float DialogHeight = 560f;

    public required Action OnClose { get; init; }

    protected override DialogState CreateState(Context ctx) => new(OnClose);

    protected override IWidget Build(Context ctx, DialogState state)
    {
        var vm = new KeyboardShortcutsViewModel(ctx.KeyMap(), ctx.Localization());

        return new Box
        {
            Width = DialogFrame.WidthWide,
            Height = DialogHeight,
            BorderSize = BorderSizeStyle.All(1),
            BorderRadius = BorderRadiusStyle.All(DialogFrame.DefaultBorderRadius),
            Background = Theme.Color(s => s.DialogFrame.Background),
            BorderColor = Theme.BorderColor(s => BorderColorStyle.All(s.DialogFrame.Border)),
            Children =
            [
                new Padding
                {
                    Amount = PaddingStyle.All(DialogFrame.DefaultPadding),
                    Children =
                    [
                        new Column
                        {
                            Gap = Spacing.Lg,
                            CrossAxis = CrossAxisAlignment.Stretch,
                            Children =
                            [
                                new Row
                                {
                                    Height = Sizes.ControlHeight,
                                    CrossAxis = CrossAxisAlignment.Center,
                                    Children =
                                    [
                                        new Grow
                                        {
                                            Child = new Text
                                            {
                                                Value = L.T(s => s.ShortcutsTitle),
                                                FontSize = FontSize.Title,
                                                VAlign = TextAlignment.Center,
                                                Color = Theme.Color(s => s.DialogFrame.TitleText),
                                            },
                                        },
                                        new DialogCloseButton { OnClose = OnClose },
                                    ],
                                },
                                new Text
                                {
                                    Value = L.T(s => s.ShortcutsDescription),
                                    Wrap = TextWrap.Wrap,
                                    FontSize = FontSize.Caption,
                                    Color = Theme.Color(s => s.Palette.TextMuted),
                                },
                                new Grow
                                {
                                    Child = new DialogScrollList
                                    {
                                        Content = new Column<ShortcutSection>
                                        {
                                            Gap = Spacing.Lg,
                                            CrossAxis = CrossAxisAlignment.Stretch,
                                            Items = Prop.Bind(vm.Sections),
                                            Template = section => new ShortcutSectionWidget { Section = section },
                                        }.BuildView(ctx),
                                    },
                                },
                            ],
                        },
                    ],
                },
            ],
        };
    }
}

/// <summary>One group of the shortcuts list: its heading and the rows beneath it.</summary>
internal sealed record ShortcutSectionWidget : Widget
{
    public required ShortcutSection Section { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var rows = new List<IWidget>(Section.Rows.Count + 1)
        {
            new Text
            {
                Value = Section.Title,
                FontSize = FontSize.Caption,
                Weight = FontWeight.Bold,
                Color = Theme.Color(s => s.DialogBody.SectionHeaderText),
            },
        };
        foreach (var row in Section.Rows)
            rows.Add(new ShortcutRowWidget { Row = row });

        return new Column
        {
            Gap = Spacing.Xs,
            CrossAxis = CrossAxisAlignment.Stretch,
            Children = rows.ToArray(),
        };
    }
}

/// <summary>One command: its name leading, its caps trailing.</summary>
internal sealed record ShortcutRowWidget : Widget
{
    public required ShortcutRow Row { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var caps = new IWidget[Row.Caps.Count];
        for (var i = 0; i < caps.Length; i++)
            caps[i] = new KeyCap { Value = Row.Caps[i] };

        return new Row
        {
            Height = Sizes.RowHeight,
            Gap = Spacing.Md,
            CrossAxis = CrossAxisAlignment.Center,
            Children =
            [
                new Grow
                {
                    Child = new Text
                    {
                        Value = Row.Label,
                        VAlign = TextAlignment.Center,
                        Color = Theme.Color(s => s.DialogBody.RowText),
                    },
                },
                new Row
                {
                    Gap = Spacing.Xs,
                    CrossAxis = CrossAxisAlignment.Center,
                    Children = caps,
                },
            ],
        };
    }
}
