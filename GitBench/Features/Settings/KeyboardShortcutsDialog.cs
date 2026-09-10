using GitBench.Controls.Dialogs;
using GitBench.Input;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Components.Controls;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Settings;

/// <summary>The key map as a searchable reference: every command grouped by surface, with its caps.</summary>
internal sealed record KeyboardShortcutsDialog : Widget<DialogState>
{
    public const string SearchInputId = "shortcuts-search";

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
                                new SearchInputBox
                                {
                                    Input = new TextInput
                                    {
                                        Id = SearchInputId,
                                        Value = vm.Query,
                                        AutoFocus = true,
                                        Placeholder = L.T(s => s.ShortcutsSearchPlaceholder),
                                        Wrap = TextWrap.NoWrap,
                                        Height = Sizes.RowHeight,
                                        VAlign = TextAlignment.Center,
                                        Background = Theme.Color(s => s.TextInput.Background),
                                        Color = Theme.Color(s => s.TextInput.Text),
                                        CaretColor = Theme.Color(s => s.TextInput.Caret),
                                        SelectionColor = Theme.Color(s => s.TextInput.Selection),
                                        PlaceholderColor = Theme.Color(s => s.TextInput.PlaceholderText),
                                    },
                                },
                                new Grow
                                {
                                    Child = new DialogScrollRegion
                                    {
                                        FillParent = true,
                                        Content = new Padding
                                        {
                                            // Keeps the cards off the scrollbar when it appears.
                                            Amount = new PaddingStyle { Right = Spacing.Sm },
                                            Children =
                                            [
                                                new Column
                                                {
                                                    Gap = Spacing.Lg,
                                                    CrossAxis = CrossAxisAlignment.Stretch,
                                                    Children =
                                                    [
                                                        new Show
                                                        {
                                                            When = vm.NoMatches,
                                                            Then = () => new Text
                                                            {
                                                                Value = L.T(s => s.ShortcutsNoMatches),
                                                                FontSize = FontSize.Caption,
                                                                Color = Theme.Color(s => s.DialogBody.RowTextMissing),
                                                            },
                                                        },
                                                        new Column<ShortcutSection>
                                                        {
                                                            Gap = Spacing.Lg,
                                                            CrossAxis = CrossAxisAlignment.Stretch,
                                                            Items = Prop.Bind(vm.Sections),
                                                            Template = section => new ShortcutSectionWidget { Section = section },
                                                        },
                                                    ],
                                                },
                                            ],
                                        },
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

/// <summary>One group of the shortcuts list: its heading over a card holding the rows.</summary>
internal sealed record ShortcutSectionWidget : Widget
{
    public required ShortcutSection Section { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var rows = new IWidget[Section.Rows.Count];
        for (var i = 0; i < rows.Length; i++)
            rows[i] = new ShortcutRowWidget { Row = Section.Rows[i] };

        return new Column
        {
            Gap = Spacing.Sm,
            CrossAxis = CrossAxisAlignment.Stretch,
            Children =
            [
                new Text
                {
                    Value = Section.Title,
                    FontSize = FontSize.Body,
                    Weight = FontWeight.Bold,
                    Color = Theme.Color(s => s.Palette.TextPrimary),
                },
                new DialogInsetCard
                {
                    Children =
                    [
                        new Padding
                        {
                            Amount = new PaddingStyle { Left = Spacing.Md, Right = Spacing.Md, Top = Spacing.Sm, Bottom = Spacing.Sm },
                            Children =
                            [
                                new Column
                                {
                                    Gap = Spacing.Xs,
                                    CrossAxis = CrossAxisAlignment.Stretch,
                                    Children = rows,
                                },
                            ],
                        },
                    ],
                },
            ],
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
