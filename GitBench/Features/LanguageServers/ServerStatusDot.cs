using GitBench.Lsp.Lifecycle;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Widgets;

namespace GitBench.Features.LanguageServers;

internal sealed record ServerStatusDot : Widget
{
    public const float Size = 8f;

    public required Prop<ServerState> State { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var theme = ctx.Theme();
        var state = State.ToReadable(ctx);

        return new Box
        {
            Width = Size,
            Height = Size,
            BorderRadius = BorderRadiusStyle.All(Size / 2),
            Background = Prop.Bind(() => Color(state.Value, theme.Styles.Value)),
        };
    }

    private static uint Color(ServerState state, ThemeStyles s) => state switch
    {
        ServerState.Ready => s.Status.Success,
        ServerState.Starting or ServerState.Indexing => s.Status.Warning,
        _ => s.Status.Danger,
    };
}
