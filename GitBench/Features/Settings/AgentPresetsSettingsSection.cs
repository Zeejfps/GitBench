using GitBench.App;
using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Features.AgentConnections;
using GitBench.Localization;
using GitBench.Messages;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Settings;

/// <summary>
/// The settings page's agent presets: the list of presets with an add button, and the fields of
/// the one picked — its name, which agent it runs, what that agent may do without asking, and the
/// extra arguments it is given.
/// </summary>
internal sealed record AgentPresetsSettingsSection : Widget
{
    private const float ListWidth = 210f;

    protected override IWidget Build(Context ctx)
    {
        var loc = ctx.Localization();
        var vm = new AgentPresetsSettingsViewModel(ctx.Require<PreferencesService>(), ctx.Require<IMessageBus>(), loc);

        return new Provide<AgentPresetsSettingsViewModel>
        {
            Value = vm,
            Child = new Row
            {
                Gap = Spacing.Lg,
                CrossAxis = CrossAxisAlignment.Start,
                Children =
                [
                    new AgentPresetList { Width = ListWidth },
                    new Grow { Child = new AgentPresetFields() },
                ],
            },
        }.BindVm(vm);
    }
}

/// <summary>The presets card: a "Presets" header with an add button, then a row per preset.</summary>
internal sealed record AgentPresetList : Widget
{
    protected override IWidget Build(Context ctx)
    {
        var vm = ctx.Require<AgentPresetsSettingsViewModel>();
        var addButton = new IconButtonWidget
        {
            Icon = LucideIcons.Plus,
            IconSize = 15f,
            Width = 24,
            Height = 24,
            Command = vm.Add,
            Surface = st => Theme.Color(t => t.HeaderActionButton.Surface(st)),
            Foreground = st => Theme.Color(t => t.HeaderActionButton.Icon(st)),
        }
            .WithTooltip(L.T(t => t.AgentPresetsAdd))
            .WithController<KbmController>();

        var header = new Box
        {
            Height = 36,
            BorderSize = new BorderSizeStyle { Bottom = 1 },
            BorderColor = Theme.BorderColor(t => new BorderColorStyle { Bottom = t.DialogFrame.Border }),
            Children =
            [
                new Padding
                {
                    Amount = new PaddingStyle { Left = Spacing.Md, Right = Spacing.Xs },
                    Children =
                    [
                        new Row
                        {
                            CrossAxis = CrossAxisAlignment.Center,
                            MainAxis = MainAxisAlignment.SpaceBetween,
                            Children =
                            [
                                new Text
                                {
                                    Value = L.T(t => t.AgentPresetsListHeader),
                                    Weight = FontWeight.Bold,
                                    VAlign = TextAlignment.Center,
                                    Color = Theme.Color(t => t.Palette.TextStrong),
                                },
                                addButton,
                            ],
                        },
                    ],
                },
            ],
        };

        return new DialogInsetCard
        {
            Width = Width,
            Children =
            [
                new Column
                {
                    CrossAxis = CrossAxisAlignment.Stretch,
                    Children =
                    [
                        header,
                        new Padding
                        {
                            Amount = PaddingStyle.All(Spacing.Xs),
                            Children =
                            [
                                new Each<AgentPreset>
                                {
                                    Items = vm.Presets,
                                    Template = new AgentPresetListRow(),
                                    Gap = Spacing.Hair,
                                    CrossAxis = CrossAxisAlignment.Stretch,
                                },
                            ],
                        },
                    ],
                },
            ],
        };
    }
}

/// <summary>One preset in the list: its name over the agent it runs and what that agent may do.</summary>
internal sealed record AgentPresetListRow : Widget
{
    protected override IWidget Build(Context ctx)
    {
        var preset = ctx.Require<AgentPreset>();
        var vm = ctx.Require<AgentPresetsSettingsViewModel>();
        var s = ctx.Localization().Strings.Value;
        return new SelectableListRow
        {
            Title = preset.Name,
            Caption = $"{AgentKinds.Label(preset.Kind)} · {AgentPresetFields.PermissionLabel(preset.Permission, s)}",
            IsSelected = () => vm.SelectedId.Value == preset.Id,
            OnSelect = () => vm.Select(preset.Id),
            Delete = new Command(() => vm.Delete(preset.Id), vm.CanDelete),
            DeleteTooltip = L.T(t => t.AgentPresetsDelete),
        };
    }
}

/// <summary>The picked preset's fields.</summary>
internal sealed record AgentPresetFields : Widget
{
    public const string NameId = "settings-agent-preset-name";
    public const string ArgumentsId = "settings-agent-preset-arguments";

    protected override IWidget Build(Context ctx)
    {
        var vm = ctx.Require<AgentPresetsSettingsViewModel>();
        var s = ctx.Localization().Strings.Value;
        return new Column
        {
            Gap = 12f,
            CrossAxis = CrossAxisAlignment.Stretch,
            Children =
            [
                new AgentPresetTextField
                {
                    FieldId = NameId,
                    Label = s.AgentPresetsName,
                    Value = vm.Name,
                    Status = vm.NameStatus,
                    Invalid = vm.NameInvalid,
                },
                new LabeledRow
                {
                    Label = s.AgentPresetsAgent,
                    Value = new OptionDropdown<AgentKind>
                    {
                        Selected = vm.Kind,
                        Options = [.. AgentKinds.All.Select(k => (k, AgentKinds.Label(k), AgentKinds.Detail(k, s)))],
                    },
                },
                new LabeledRow
                {
                    Label = s.AgentPresetsPermissions,
                    Value = new OptionDropdown<AgentPermission>
                    {
                        Selected = vm.Permission,
                        Options =
                        [
                            (AgentPermission.Ask, s.AgentPresetsPermissionAsk, s.AgentPresetsPermissionAskDetail),
                            (AgentPermission.AcceptEdits, s.AgentPresetsPermissionAcceptEdits, s.AgentPresetsPermissionAcceptEditsDetail),
                            (AgentPermission.Bypass, s.AgentPresetsPermissionBypass, s.AgentPresetsPermissionBypassDetail),
                        ],
                    },
                },
                new AgentPresetTextField
                {
                    FieldId = ArgumentsId,
                    Label = s.AgentPresetsArguments,
                    Value = vm.Arguments,
                    Placeholder = "--model opus --effort high",
                    Hint = s.AgentPresetsArgumentsHint,
                    Status = vm.ArgumentsStatus,
                    Invalid = vm.ArgumentsInvalid,
                },
            ],
        };
    }

    public static string PermissionLabel(AgentPermission permission, Strings s) => permission switch
    {
        AgentPermission.Ask => s.AgentPresetsPermissionAsk,
        AgentPermission.AcceptEdits => s.AgentPresetsPermissionAcceptEdits,
        AgentPermission.Bypass => s.AgentPresetsPermissionBypass,
        _ => throw new ArgumentOutOfRangeException(nameof(permission), permission, "Unknown permission."),
    };
}

/// <summary>A preset field: its label over the box, then a hint, then why the text isn't saved
/// while it isn't.</summary>
internal sealed record AgentPresetTextField : Widget
{
    public required string FieldId { get; init; }
    public required string Label { get; init; }
    public required State<string> Value { get; init; }
    public required IReadable<FieldStatus?> Status { get; init; }
    public required IReadable<bool> Invalid { get; init; }
    public string? Placeholder { get; init; }
    public string? Hint { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var status = Status;
        List<IWidget> children =
        [
            new Text { Value = Label, Color = Theme.Color(t => t.DialogBody.SectionHeaderText) },
            new SettingsTextField
            {
                FieldId = FieldId,
                Value = Value,
                Placeholder = Placeholder,
                Invalid = Invalid,
            },
        ];
        if (Hint is { } hint)
            children.Add(new Text
            {
                Value = hint,
                Wrap = TextWrap.Wrap,
                FontSize = FontSize.Caption,
                Color = Theme.Color(t => t.Palette.TextMuted),
            });
        children.Add(new Text
        {
            Value = status.Bind(s => s?.Message),
            Wrap = TextWrap.Wrap,
            FontSize = FontSize.Caption,
            Visible = Prop.Bind(() => status.Value is not null),
            Color = Theme.Color(t => t.DialogFrame.ErrorText),
        });

        return new Column
        {
            Gap = Spacing.Xs,
            CrossAxis = CrossAxisAlignment.Stretch,
            Children = [.. children],
        };
    }
}
