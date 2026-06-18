using GitBench.Controls;
using GitBench.Git;
using ZGF.Gui;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Repos;

// A nested repo row — a worktree or submodule under its primary. Branch/package glyph, shorter,
// activate-on-click (the parent attaches NavigableRowController). Composes the shared RepoRowShell.
internal sealed record NavigableRepoRow : Widget<RepoRowState>
{
    protected override RepoRowState CreateState(Context ctx) => new(ctx.Require<RepoNodeViewModel>());

    protected override IWidget Build(Context ctx, RepoRowState state)
    {
        var glyph = ctx.Require<RepoNodeViewModel>().Kind == RepoKind.Worktree
            ? LucideIcons.Branch
            : LucideIcons.Package;

        return new RepoRowShell
        {
            Glyph = glyph,
            RowHeight = 26,
            GlyphSize = 13f,
            Hovered = state.Hovered,
        };
    }
}
