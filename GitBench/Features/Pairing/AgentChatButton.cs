using GitBench.Controls;
using GitBench.Features.Repos;
using GitBench.Input;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Pairing;

/// <summary>
/// The agent chat's entry point in the actions toolbar. A click shows or hides the repository's
/// conversation, opening one with the agent picked last; until one is picked, and on a right-click,
/// it asks which agent.
/// </summary>
internal sealed record AgentChatButton : Widget
{
    public const string ButtonId = "agent-chat-button";

    protected override IWidget Build(Context ctx)
    {
        var chat = ctx.Require<AgentChat>();
        var loc = ctx.Localization();
        var keys = ctx.KeyMap();

        void ShowAgents(RectF rect) => RepoBarContextMenu.Show(ctx, rect.BottomLeft, chat.AgentMenu());

        var button = new ButtonWidget
        {
            Id = ButtonId,
            // The presses belong to the controller below; this gives the button its enabled state.
            Command = new Command(static () => { }, chat.IsAvailable),
            ContentInset = ButtonStyle.Plain.IconOnlyInset,
            Children = [new ButtonIcon { Value = LucideIcons.Sparkles }],
        };

        return button
            .WithTooltip(Prop.Bind<string?>(() =>
                $"{loc.Strings.Value.AgentChatOpen} ({keys.Display(KeyCommand.ToggleAssistant)})\n{loc.Strings.Value.AgentChatPickHint}"))
            .WithController((_, view) => new AgentChatButtonController(button.State, view, rect =>
            {
                if (chat.Press() is AgentChatPress.NeedsAgent) ShowAgents(rect);
            }, ShowAgents));
    }
}

/// <summary>Drives the agent chat button's hover, and tells a left press from a right one.</summary>
internal sealed class AgentChatButtonController(IInteractable target, View view, Action<RectF> onPress, Action<RectF> onMenu)
    : KeyboardMouseController
{
    public override void OnMouseEnter(ref MouseEnterEvent e)
    {
        if (target.Enabled.Value) target.Hovered.Value = true;
    }

    public override void OnMouseExit(ref MouseExitEvent e) => target.Hovered.Value = false;

    public override void OnMouseButtonStateChanged(ref MouseButtonEvent e)
    {
        if (!target.Enabled.Value || e.Phase != EventPhase.Bubbling || e.State != InputState.Pressed) return;
        if (e.Button == MouseButton.Left) onPress(view.Position);
        else if (e.Button == MouseButton.Right) onMenu(view.Position);
        else return;
        e.Consume();
    }
}
