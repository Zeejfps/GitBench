using GitBench.Controls;
using GitBench.Features.AgentConnections;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.StatusBar;

/// <summary>
/// Shows how many agents are connected over MCP while at least one is, and nothing otherwise —
/// an agent that can read the review and drive its window is worth a glance at the bar.
/// </summary>
internal sealed record AgentConnectionsBadge : Widget
{
    public const string BadgeId = "statusbar-agent-connections";

    protected override IWidget Build(Context ctx)
    {
        var state = ctx.Require<State<AgentConnectionState>>();
        var loc = ctx.Localization();
        var sessions = new Derived<int>(() => state.Value is AgentConnectionState.Listening listening ? listening.Sessions : 0);

        return new Row
        {
            Id = BadgeId,
            Gap = Spacing.Xs,
            CrossAxis = CrossAxisAlignment.Center,
            Visible = sessions.Bind(n => n > 0),
            Children =
            [
                new Text
                {
                    Value = LucideIcons.SquareTerminal,
                    FontFamily = LucideIcons.FontFamily,
                    FontSize = FontSize.Body,
                    VAlign = TextAlignment.Center,
                    Color = Theme.Color(s => s.StatusBar.Text),
                },
                new Text
                {
                    Value = Prop.Bind<string?>(() => loc.Strings.Value.StatusbarAgentsConnected(sessions.Value)),
                    FontSize = FontSize.Caption,
                    VAlign = TextAlignment.Center,
                    Color = Theme.Color(s => s.StatusBar.Text),
                },
            ],
        }.BindVm(sessions);
    }
}
