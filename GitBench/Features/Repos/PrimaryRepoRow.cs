using GitBench.Controls;
using ZGF.Gui;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Repos;

// The top-level repo row: folder glyph, taller, drag-to-reorder (the parent attaches
// RepoRowController). Composes the shared RepoRowShell over a RepoRowState.
internal sealed record PrimaryRepoRow : Widget<RepoRowState>
{
    protected override RepoRowState CreateState(Context ctx) => new(ctx.Require<RepoNodeViewModel>());

    protected override IWidget Build(Context ctx, RepoRowState state) => new RepoRowShell
    {
        Glyph = LucideIcons.FolderGit2,
        RowHeight = 28,
        GlyphSize = 14f,
        Hovered = state.Hovered,
    };
}
