using GitBench.Controls;
using GitBench.Features.Repos;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Components.Controls;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Assistant;

/// <summary>
/// Where the assistant is pointed: the provider, the model and endpoint chosen for it, and the key
/// it is signed with. Takes the composer's place — as onboarding while nothing is configured, and on
/// demand afterwards.
/// </summary>
internal sealed record AssistantSettingsCard : Widget
{
    public const string ProviderId = "assistant-provider";
    public const string ModelInputId = "assistant-model-input";
    public const string BaseUrlInputId = "assistant-base-url-input";
    public const string KeyInputId = "assistant-key-input";
    public const string SaveId = "assistant-key-save";
    public const string CancelId = "assistant-settings-cancel";

    protected override IWidget Build(Context ctx)
    {
        var vm = ctx.Require<AssistantViewModel>();
        var loc = ctx.Localization();

        return new Box
        {
            BorderSize = new BorderSizeStyle { Top = 1 },
            BorderColor = Theme.BorderColor(s => new BorderColorStyle { Top = s.Palette.Border }),
            Children =
            [
                new Padding
                {
                    Amount = PaddingStyle.All(Spacing.Lg),
                    Children =
                    [
                        new Column
                        {
                            Gap = Spacing.Sm,
                            CrossAxis = CrossAxisAlignment.Stretch,
                            Children =
                            [
                                new Text
                                {
                                    Value = Prop.Bind<string?>(() => vm.NeedsSetup.Value
                                        ? loc.Strings.Value.AssistantSetupTitle
                                        : loc.Strings.Value.AssistantSettingsTitle),
                                    Weight = FontWeight.Bold,
                                    FontSize = FontSize.Body,
                                    Color = Theme.Color(s => s.Palette.TextPrimary),
                                },
                                new Text
                                {
                                    Value = L.T(s => s.AssistantSetupBody),
                                    Wrap = TextWrap.Wrap,
                                    FontSize = FontSize.Caption,
                                    Color = Theme.Color(s => s.Palette.TextMuted),
                                    Visible = Prop.Bind(vm.NeedsSetup),
                                },
                                new AssistantProviderPicker(),
                                new AssistantSettingsField
                                {
                                    FieldId = ModelInputId,
                                    Label = L.T(s => s.AssistantSettingsModel),
                                    Value = vm.ModelDraft,
                                    Placeholder = Prop.Bind<string?>(() => vm.ModelHint.Value),
                                    Trailing = new AssistantModelPresetPicker(),
                                },
                                new Show
                                {
                                    When = vm.WantsBaseUrl,
                                    Then = () => new AssistantSettingsField
                                    {
                                        FieldId = BaseUrlInputId,
                                        Label = L.T(s => s.AssistantSettingsBaseUrl),
                                        Value = vm.BaseUrlDraft,
                                        Placeholder = Prop.Bind<string?>(() => vm.BaseUrlHint.Value),
                                    },
                                },
                                new Show
                                {
                                    When = vm.WantsApiKey,
                                    Then = () => new AssistantSettingsField
                                    {
                                        FieldId = KeyInputId,
                                        Label = L.T(s => s.AssistantSettingsKey),
                                        Value = vm.KeyDraft,
                                        // The label column is a fixed width, so which of the two a
                                        // key is — asked for, or merely taken — is said in the box.
                                        Placeholder = Prop.Bind<string?>(() => vm.IsApiKeyOptional.Value
                                            ? loc.Strings.Value.AssistantSetupPlaceholderOptional
                                            : loc.Strings.Value.AssistantSetupPlaceholder),
                                        Masked = true,
                                    },
                                },
                                new Text
                                {
                                    Value = Prop.Bind<string?>(() => vm.KeyHint.Value),
                                    Wrap = TextWrap.Wrap,
                                    FontSize = FontSize.Caption,
                                    Color = Theme.Color(s => s.Palette.TextMuted),
                                    // A saved key is in the field, and an empty line for it would
                                    // leave a gap where prose used to be.
                                    Visible = Prop.Bind(() => vm.KeyHint.Value.Length > 0),
                                },
                                new Row
                                {
                                    Gap = Spacing.Sm,
                                    MainAxis = MainAxisAlignment.End,
                                    Children =
                                    [
                                        new ButtonWidget
                                        {
                                            Id = CancelId,
                                            Style = ButtonStyle.Outline(static s => s.Palette.TextMuted),
                                            Command = vm.CloseSettings,
                                            Visible = Prop.Bind(() => !vm.NeedsSetup.Value),
                                            Children =
                                            [
                                                new ButtonLabel { Value = L.T(s => s.AssistantSettingsCancel) },
                                            ],
                                        }.WithController<KbmController>(),
                                        new ButtonWidget
                                        {
                                            Id = SaveId,
                                            Style = ButtonStyle.Filled(static s => s.Palette.Accent),
                                            Command = vm.SaveSettings,
                                            Children =
                                            [
                                                new ButtonLabel { Value = L.T(s => s.AssistantSetupSave) },
                                            ],
                                        }.WithController<KbmController>(),
                                    ],
                                },
                            ],
                        },
                    ],
                },
            ],
        };
    }
}

/// <summary>Which provider the assistant talks to, as a labelled select over the provider registry.</summary>
internal sealed record AssistantProviderPicker : Widget
{
    protected override IWidget Build(Context ctx)
    {
        var vm = ctx.Require<AssistantViewModel>();

        return new Row
        {
            Gap = Spacing.Md,
            CrossAxis = CrossAxisAlignment.Center,
            Children =
            [
                new AssistantSettingsLabel { Value = L.T(s => s.AssistantSettingsProvider) },
                new Grow
                {
                    Child = new DropdownWidget
                    {
                        Id = AssistantSettingsCard.ProviderId,
                        Children =
                        [
                            new Grow
                            {
                                Child = new Text
                                {
                                    Value = Prop.Bind<string?>(() => vm.ProviderName.Value),
                                    FontSize = FontSize.Caption,
                                    VAlign = TextAlignment.Center,
                                    Color = Theme.Color(s => s.Palette.TextPrimary),
                                },
                            },
                        ],
                    }.WithMenuController(rect =>
                        RepoBarContextMenu.Show(ctx, rect.BottomLeft, vm.BuildProviderMenu())),
                },
            ],
        };
    }
}

/// <summary>The caption column a connection line opens with. One width for every line, so the fields
/// beside them share an edge.</summary>
internal sealed record AssistantSettingsLabel : Widget
{
    internal const float Column = 72f;

    public required Prop<string?> Value { get; init; }

    protected override IWidget Build(Context ctx) => new Text
    {
        Value = Value,
        FontSize = FontSize.Caption,
        VAlign = TextAlignment.Center,
        Width = Column,
        Color = Theme.Color(s => s.Palette.TextMuted),
    };
}

/// <summary>
/// Offers the draft provider's own model ids for the model field. A default and not a whitelist:
/// picking one fills the field in, and the field stays free text for anything unlisted.
/// </summary>
internal sealed record AssistantModelPresetPicker : Widget
{
    public const string PickerId = "assistant-model-presets";

    protected override IWidget Build(Context ctx)
    {
        var vm = ctx.Require<AssistantViewModel>();

        return new Show
        {
            When = vm.HasModelPresets,
            Then = () => new DropdownWidget
            {
                Id = PickerId,
                Children = [],
            }
                .WithTooltip(L.T(s => s.AssistantSettingsModelPresets))
                .WithMenuController(rect =>
                    RepoBarContextMenu.Show(ctx, rect.BottomLeft, vm.BuildModelMenu())),
        };
    }
}

/// <summary>One labelled line of the connection: a caption, the field that carries it, and whatever
/// control helps fill it in.</summary>
internal sealed record AssistantSettingsField : Widget
{
    public required string FieldId { get; init; }
    public required Prop<string?> Label { get; init; }
    public required State<string> Value { get; init; }
    public Prop<string?> Placeholder { get; init; }

    /// <summary>Draws the value as bullets — for the key, which is a secret on screen as much as at rest.</summary>
    public bool Masked { get; init; }

    /// <summary>Shown after the field, for a control that fills it in.</summary>
    public IWidget? Trailing { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var label = new AssistantSettingsLabel { Value = Label };

        var field = new Grow
        {
            Child = new Box
            {
                Background = Theme.Color(s => s.TextInput.Background),
                BorderColor = Theme.BorderColor(s => BorderColorStyle.All(s.TextInput.Border)),
                BorderSize = BorderSizeStyle.All(1),
                BorderRadius = BorderRadiusStyle.All(Radius.Sm),
                Children =
                [
                    new Padding
                    {
                        Amount = new PaddingStyle
                        {
                            Left = Spacing.Sm, Right = Spacing.Sm, Top = Spacing.Xs, Bottom = Spacing.Xs,
                        },
                        Children =
                        [
                            new TextInput
                            {
                                Id = FieldId,
                                Value = Value,
                                Masked = Masked,
                                Placeholder = Placeholder,
                                Wrap = TextWrap.NoWrap,
                                Background = Theme.Color(s => s.TextInput.Background),
                                Color = Theme.Color(s => s.TextInput.Text),
                                CaretColor = Theme.Color(s => s.TextInput.Caret),
                                SelectionColor = Theme.Color(s => s.TextInput.Selection),
                                PlaceholderColor = Theme.Color(s => s.TextInput.PlaceholderText),
                            },
                        ],
                    },
                ],
            },
        };

        IWidget[] children = Trailing is { } trailing ? [label, field, trailing] : [label, field];

        return new Row
        {
            Gap = Spacing.Md,
            CrossAxis = CrossAxisAlignment.Center,
            Children = children,
        };
    }
}
