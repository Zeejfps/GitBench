using GitBench.App;
using GitBench.Features.Toolbar;
using GitBench.Localization;
using ZGF.Gui;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Assistant;

/// <summary>
/// The assistant's entry point in the actions toolbar: the dino mark where the other trailing icon
/// buttons put their glyph. Disabled — like its neighbours — when no repository is active, since the
/// assistant's tools are built against one checkout.
/// </summary>
internal sealed record AssistantToolbarButton : Widget
{
    public const string ButtonId = "assistant-toolbar-button";

    private const int MarkSize = 16;

    protected override IWidget Build(Context ctx) => new ToolbarIconButton
    {
        Id = ButtonId,
        Command = ctx.Require<AssistantViewModel>().Toggle,
        Content = new AssistantMark { Size = MarkSize },
        Tooltip = L.T(s => s.AssistantOpen),
    };
}
