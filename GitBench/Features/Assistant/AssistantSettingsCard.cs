using GitBench.Controls;
using GitBench.Features.Assistant.Backend;
using GitBench.Features.Repos;
using GitBench.Features.Settings;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Assistant;

/// <summary>
/// Where the assistant is pointed: the key and endpoint each provider is given, and the provider
/// and model each role runs on. The settings window's assistant page.
/// </summary>
internal sealed record AssistantSettingsCard : Widget
{
    public const string ProviderId = "assistant-provider";
    public const string BaseUrlInputId = "assistant-base-url-input";
    public const string KeyInputId = "assistant-key-input";
    public const string SaveId = "assistant-key-save";
    public const string CancelId = "assistant-settings-cancel";

    /// <summary>The roles something in the app runs on: the commit message and the review
    /// walkthrough. The agent chat runs on an ACP agent instead.</summary>
    public static readonly IReadOnlyList<AssistantRole> Roles = [AssistantRole.CommitMessage, AssistantRole.Walkthrough];

    public static string RoleProviderId(AssistantRole role) => $"assistant-{AssistantRoles.Id(role)}-provider";
    public static string RoleModelInputId(AssistantRole role) => $"assistant-{AssistantRoles.Id(role)}-model-input";
    public static string RoleModelPresetsId(AssistantRole role) => $"assistant-{AssistantRoles.Id(role)}-model-presets";

    protected override IWidget Build(Context ctx)
    {
        var vm = ctx.Require<AssistantViewModel>();
        var loc = ctx.Localization();

        return new Column
        {
            Gap = Spacing.Sm,
            CrossAxis = CrossAxisAlignment.Stretch,
            Children =
            [
                new AssistantSettingsCaption { Value = L.T(s => s.AssistantSettingsKeys) },
                new AssistantProviderPicker(),
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
                // Every provider takes a key: a self-hosted endpoint needs none,
                // but a gateway put in front of one is routinely behind a token,
                // and without this field the only box left for it is the endpoint.
                new AssistantSettingsField
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
                new AssistantSettingsCaption { Value = L.T(s => s.AssistantSettingsModels) },
                .. Roles.Select(role => (IWidget)new AssistantRoleLine { Role = role }),
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
                            Command = vm.ResetSettings,
                            Children =
                            [
                                new ButtonLabel { Value = L.T(s => s.SettingsAgentReset) },
                            ],
                        }.WithController<KbmController>(),
                        new ButtonWidget
                        {
                            Id = SaveId,
                            Style = ButtonStyle.Filled(static s => s.Palette.Accent),
                            Command = vm.SaveSettings,
                            Children =
                            [
                                new ButtonLabel { Value = L.T(s => s.SettingsAgentSave) },
                            ],
                        }.WithController<KbmController>(),
                    ],
                },
            ],
        };
    }
}

/// <summary>Which provider the key and endpoint fields below are for, as a labelled select over the
/// provider registry.</summary>
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
                    Child = new AssistantProviderDropdown
                    {
                        Id = AssistantSettingsCard.ProviderId,
                        Value = vm.KeyProviderName,
                        Menu = vm.BuildProviderMenu,
                    },
                },
            ],
        };
    }
}

/// <summary>A provider's name with a chevron, opening the list to pick another.</summary>
internal sealed record AssistantProviderDropdown : Widget
{
    public required IReadable<string> Value { get; init; }

    public required Func<IReadOnlyList<RepoBarContextMenu.Item>> Menu { get; init; }

    protected override IWidget Build(Context ctx) => new DropdownWidget
    {
        Children =
        [
            new Grow
            {
                Child = new Text
                {
                    Value = Prop.Bind<string?>(() => Value.Value),
                    FontSize = FontSize.Body,
                    VAlign = TextAlignment.Center,
                    Color = Theme.Color(s => s.Palette.TextPrimary),
                },
            },
        ],
    }.WithMenuController(rect => RepoBarContextMenu.Show(ctx, rect.BottomLeft, Menu()));
}

/// <summary>One role's line: its name, the provider it runs on, and the model typed for it with the
/// provider's own on offer.</summary>
internal sealed record AssistantRoleLine : Widget
{
    private const float ProviderColumn = 104f;

    public required AssistantRole Role { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var vm = ctx.Require<AssistantViewModel>();
        var draft = vm.RoleDraft(Role);

        return new AssistantSettingsField
        {
            FieldId = AssistantSettingsCard.RoleModelInputId(Role),
            Label = L.T(s => AssistantKeyLabels.RoleName(Role, s)),
            Value = draft.Model,
            Placeholder = Prop.Bind<string?>(() => draft.ModelHint.Value),
            Leading = new AssistantProviderDropdown
            {
                Id = AssistantSettingsCard.RoleProviderId(Role),
                Width = ProviderColumn,
                Value = draft.ProviderName,
                Menu = draft.BuildProviderMenu,
            },
            Trailing = new AssistantModelPresetPicker { Role = Role },
        };
    }
}

/// <summary>A section's heading inside the card, so the keys and the models read as two things.</summary>
internal sealed record AssistantSettingsCaption : Widget
{
    public required Prop<string?> Value { get; init; }

    protected override IWidget Build(Context ctx) => new Padding
    {
        Amount = new PaddingStyle { Top = Spacing.Sm },
        Children =
        [
            new Text
            {
                Value = Value,
                FontSize = FontSize.Caption,
                Weight = FontWeight.Bold,
                Color = Theme.Color(s => s.Palette.TextMuted),
            },
        ],
    };
}

/// <summary>The caption column a connection line opens with. One width for every line, so the fields
/// beside them share an edge.</summary>
internal sealed record AssistantSettingsLabel : Widget
{
    internal const float Column = 96f;

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
/// Offers a role's provider's own model ids for its model field. A default and not a whitelist:
/// picking one fills the field in, and the field stays free text for anything unlisted.
/// </summary>
internal sealed record AssistantModelPresetPicker : Widget
{
    public required AssistantRole Role { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var draft = ctx.Require<AssistantViewModel>().RoleDraft(Role);

        return new Show
        {
            When = draft.HasModelPresets,
            Then = () => new DropdownWidget
            {
                Id = AssistantSettingsCard.RoleModelPresetsId(Role),
                Children = [],
            }
                .WithTooltip(L.T(s => s.AssistantSettingsModelPresets))
                .WithMenuController(rect =>
                    RepoBarContextMenu.Show(ctx, rect.BottomLeft, draft.BuildModelMenu())),
        };
    }
}

/// <summary>One labelled line of the card: a caption, the field that carries it, and whatever
/// controls sit beside it.</summary>
internal sealed record AssistantSettingsField : Widget
{
    public required string FieldId { get; init; }
    public required Prop<string?> Label { get; init; }
    public required State<string> Value { get; init; }
    public Prop<string?> Placeholder { get; init; }

    /// <summary>Draws the value as bullets — for the key, which is a secret on screen as much as at rest.</summary>
    public bool Masked { get; init; }

    /// <summary>Shown between the caption and the field, for a choice the field depends on.</summary>
    public IWidget? Leading { get; init; }

    /// <summary>Shown after the field, for a control that fills it in.</summary>
    public IWidget? Trailing { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var label = new AssistantSettingsLabel { Value = Label };

        var field = new Grow
        {
            Child = new SettingsTextField
            {
                FieldId = FieldId,
                Value = Value,
                Masked = Masked,
                Placeholder = Placeholder,
            },
        };

        var children = new List<IWidget> { label };
        if (Leading is { } leading) children.Add(leading);
        children.Add(field);
        if (Trailing is { } trailing) children.Add(trailing);

        return new Row
        {
            Gap = Spacing.Md,
            CrossAxis = CrossAxisAlignment.Center,
            Children = [.. children],
        };
    }
}
