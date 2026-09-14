using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Features.AgentConnections;
using GitBench.Localization;
using GitBench.Messages;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Components.Controls;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Settings;

/// <summary>
/// The settings dialog's agent-connections rows: the on/off toggle, the port, where the server
/// listens (or why it does not), and the button that copies the <c>claude mcp add</c> command.
/// </summary>
internal sealed record AgentConnectionsSettingsSection : Widget
{
    public const string EnabledId = "settings-agent-connections-enabled";
    public const string PortInputId = "settings-agent-connections-port";
    public const string StatusId = "settings-agent-connections-status";
    public const string CopyCommandId = "settings-agent-connections-copy";

    protected override IWidget Build(Context ctx)
    {
        var loc = ctx.Localization();
        var vm = new AgentConnectionsSettingsViewModel(
            ctx.Require<State<AgentConnectionSettings>>(),
            ctx.Require<State<AgentConnectionState>>(),
            ctx.Require<IClipboard>(),
            ctx.Require<IMessageBus>(),
            loc);

        return new Column
        {
            Gap = Spacing.Lg,
            CrossAxis = CrossAxisAlignment.Stretch,
            Children =
            [
                new SettingsRow
                {
                    Label = L.T(s => s.SettingsAgentConnectionsEnable),
                    Description = L.T(s => s.SettingsAgentConnectionsEnableDesc),
                    Control = new CheckboxWidget
                    {
                        Id = EnabledId,
                        Checked = vm.Enabled,
                        Height = Sizes.RowHeight,
                    }.WithController<KbmController>(),
                },
                new SettingsRow
                {
                    Label = L.T(s => s.SettingsAgentConnectionsPort),
                    Description = Prop.Bind<string?>(() => vm.PortInvalid.Value
                        ? loc.Strings.Value.SettingsAgentConnectionsPortInvalid
                        : loc.Strings.Value.SettingsAgentConnectionsPortDesc),
                    Control = new AgentConnectionsPortField { Value = vm.PortDraft, Invalid = vm.PortInvalid },
                },
                new SettingsRow
                {
                    Label = L.T(s => s.SettingsAgentConnectionsEndpoint),
                    Description = Prop.Bind<string?>(() => vm.StatusText.Value),
                    DescriptionId = StatusId,
                    Control = new SecondaryDialogButton
                    {
                        Id = CopyCommandId,
                        Label = L.T(s => s.SettingsAgentConnectionsCopyCommand),
                        Icon = LucideIcons.Copy,
                        Command = vm.CopyCommand,
                        Height = Sizes.ControlHeight,
                    }.WithController<KbmController>(),
                },
            ],
        }.BindVm(vm);
    }
}

/// <summary>The port box: a short field whose border turns to the error color while its text is
/// not a port.</summary>
internal sealed record AgentConnectionsPortField : Widget
{
    private const float FieldWidth = 96f;

    public required State<string> Value { get; init; }
    public required IReadable<bool> Invalid { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var styles = ctx.Theme().Styles;
        var invalid = Invalid;

        return new Box
        {
            Width = FieldWidth,
            Background = Theme.Color(s => s.TextInput.Background),
            BorderColor = Prop.Bind(() => BorderColorStyle.All(invalid.Value
                ? styles.Value.DialogFrame.ErrorText
                : styles.Value.TextInput.Border)),
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
                            Id = AgentConnectionsSettingsSection.PortInputId,
                            Value = Value,
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
        };
    }
}
