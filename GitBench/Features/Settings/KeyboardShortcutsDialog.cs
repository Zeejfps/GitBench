using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Input;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Components.Controls;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Settings;

/// <summary>
/// The key map as a searchable reference and editor: every command grouped by surface, with its
/// caps; click a command's caps and press the keys that should run it.
/// </summary>
internal sealed record KeyboardShortcutsDialog : Widget<DialogState>
{
    public const string SearchInputId = "shortcuts-search";
    public const string ResetAllId = "shortcuts-reset-all";

    /// <summary>The id of the caps button that starts recording a command's gesture.</summary>
    public static string CapsId(KeyCommand command) => $"shortcut-{command}";

    /// <summary>The id of the button that puts a command back on its built-in gesture.</summary>
    public static string ResetId(KeyCommand command) => $"shortcut-reset-{command}";

    private const float DialogHeight = 560f;

    public required Action OnClose { get; init; }

    protected override DialogState CreateState(Context ctx) => new(OnClose);

    protected override IWidget Build(Context ctx, DialogState state)
    {
        var input = ctx.Require<InputSystem>();
        var vm = new KeyboardShortcutsViewModel(ctx.Require<KeyMap>(), ctx.Localization());

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
                                    Child = new ScrollRegion
                                    {
                                        FillParent = true,
                                        StretchContent = true,
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
                                                            Template = section => new ShortcutSectionWidget { Model = vm, Section = section },
                                                        },
                                                    ],
                                                },
                                            ],
                                        },
                                    },
                                },
                                new Row
                                {
                                    MainAxis = MainAxisAlignment.End,
                                    CrossAxis = CrossAxisAlignment.Center,
                                    Visible = Prop.Bind(vm.HasOverrides),
                                    Children =
                                    [
                                        new SecondaryDialogButton
                                        {
                                            Id = ResetAllId,
                                            Label = L.T(s => s.ShortcutsResetAll),
                                            Command = new Command(vm.ResetAll),
                                            Height = Sizes.ControlHeight,
                                        }.WithController<KbmController>(),
                                    ],
                                },
                            ],
                        },
                    ],
                },
            ],
        }.WithController(input, () => new ShortcutRecorderController(vm, input));
    }
}

/// <summary>One group of the shortcuts list: its heading over a card holding the rows.</summary>
internal sealed record ShortcutSectionWidget : Widget
{
    public required KeyboardShortcutsViewModel Model { get; init; }
    public required ShortcutSection Section { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var rows = new IWidget[Section.Rows.Count];
        for (var i = 0; i < rows.Length; i++)
            rows[i] = new ShortcutRowWidget { Model = Model, Row = Section.Rows[i] };

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

/// <summary>
/// One command: its name leading, its caps trailing as the button that starts recording a new
/// gesture, a reset control once it is off its default, and a note under the name naming the
/// commands its keys also fire.
/// </summary>
internal sealed record ShortcutRowWidget : Widget
{
    public required KeyboardShortcutsViewModel Model { get; init; }
    public required ShortcutRow Row { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var vm = Model;
        var row = Row;
        var isRecording = new Derived<bool>(() => vm.Recording.Value == row.Command);

        var caps = new IWidget[row.Caps.Count];
        for (var i = 0; i < caps.Length; i++)
            caps[i] = new KeyCap { Value = row.Caps[i] };

        IWidget name = new Text
        {
            Value = row.Label,
            Height = Sizes.RowHeight,
            VAlign = TextAlignment.Center,
            Color = Theme.Color(s => s.DialogBody.RowText),
        };
        if (row.ConflictsWith.Count > 0)
        {
            var loc = ctx.Localization();
            name = new Column
            {
                CrossAxis = CrossAxisAlignment.Stretch,
                Children =
                [
                    name,
                    new Text
                    {
                        Value = Prop.Bind<string?>(() =>
                            loc.Strings.Value.ShortcutsConflict(string.Join(", ", row.ConflictsWith))),
                        Wrap = TextWrap.Wrap,
                        FontSize = FontSize.Caption,
                        Color = Theme.Color(s => s.Status.Warning),
                    },
                ],
            };
        }

        return new Row
        {
            Gap = Spacing.Sm,
            CrossAxis = CrossAxisAlignment.Center,
            Children =
            [
                new Grow { Child = name },
                new ButtonWidget
                {
                    Id = KeyboardShortcutsDialog.ResetId(row.Command),
                    Style = ButtonStyle.Plain,
                    Height = Sizes.RowHeight,
                    ContentInset = new PaddingStyle { Left = Spacing.Xs, Right = Spacing.Xs },
                    Command = new Command(() => vm.Reset(row.Command)),
                    Visible = !row.IsDefault,
                    Children =
                    [
                        new Text
                        {
                            FontFamily = LucideIcons.FontFamily,
                            FontSize = FontSize.Body,
                            Value = LucideIcons.Undo,
                            HAlign = TextAlignment.Center,
                            VAlign = TextAlignment.Center,
                            Color = Theme.Color(s => s.Palette.TextMuted),
                        },
                    ],
                }
                    .WithTooltip(L.T(s => s.ShortcutsResetTooltip))
                    .WithController<KbmController>(),
                new ButtonWidget
                {
                    Id = KeyboardShortcutsDialog.CapsId(row.Command),
                    Style = ButtonStyle.Plain,
                    Height = Sizes.RowHeight,
                    ContentInset = new PaddingStyle { Left = Spacing.Xs, Right = Spacing.Xs },
                    Command = new Command(() => vm.BeginRecording(row.Command)),
                    Children =
                    [
                        new Row
                        {
                            Gap = Spacing.Xs,
                            CrossAxis = CrossAxisAlignment.Center,
                            Visible = Prop.Bind(() => !isRecording.Value),
                            Children = caps,
                        },
                        new Show
                        {
                            When = isRecording,
                            Then = () => new RecordingCap(),
                        },
                    ],
                }
                    .WithTooltip(L.T(s => s.ShortcutsChangeTooltip))
                    .WithController<KbmController>(),
            ],
        };
    }
}

/// <summary>The cap a row shows in place of its keys while it waits for the next key press.</summary>
internal sealed record RecordingCap : Widget
{
    protected override IWidget Build(Context ctx) => new Box
    {
        Background = Theme.Color(s => s.Palette.SurfaceSelectedSubtle),
        BorderSize = BorderSizeStyle.All(1),
        BorderColor = Theme.BorderColor(s => BorderColorStyle.All(s.Palette.Accent)),
        BorderRadius = BorderRadiusStyle.All(4f),
        Children =
        [
            new Padding
            {
                Amount = new PaddingStyle { Left = Spacing.Sm, Right = Spacing.Sm, Top = 2, Bottom = 2 },
                Children =
                [
                    new Text
                    {
                        Value = L.T(s => s.ShortcutsRecordPrompt),
                        FontSize = FontSize.Caption,
                        Color = Theme.Color(s => s.Palette.Accent),
                        VAlign = TextAlignment.Center,
                        HAlign = TextAlignment.Center,
                    },
                ],
            },
        ],
    };
}
