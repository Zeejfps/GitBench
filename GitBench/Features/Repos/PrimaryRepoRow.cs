using GitBench.Controls;
using ZGF.Gui;
using ZGF.Gui.Widgets;
using GitBench.Widgets;

namespace GitBench.Features.Repos;

// The top-level repo row: folder glyph, taller, drag-to-reorder (the parent attaches
// RepoRowController). Composes the shared RepoRowShell over a RepoRowState.
internal sealed record PrimaryRepoRow : Widget<RepoRowState>
{
    protected override RepoRowState CreateState(Context ctx) => new(ctx.Require<RepoNodeViewModel>());

    protected override IWidget Build(Context ctx, RepoRowState state)
    {
        var vm = ctx.Require<RepoNodeViewModel>();
        return new RepoRowShell
        {
            Glyph = LucideIcons.FolderGit2,
            RowHeight = Sizes.ControlHeight,
            GlyphSize = 14f,
            Hovered = state.Hovered,
            GlyphSlot = new Box
            {
                Width = RepoBar.RowIconWidth,
                Height = 16,
                Children =
                [
                    new Text
                    {
                        Value = LucideIcons.FolderGit2,
                        FontFamily = LucideIcons.FontFamily,
                        FontSize = 14f,
                        HAlign = TextAlignment.Center,
                        VAlign = TextAlignment.Center,
                        Color = Theme.Color(s => s.RepoBarRow.Icon(vm.Kind, vm.IsActive.Value, vm.IsMissing.Value)),
                    },
                    new Switch<string?>
                    {
                        Value = vm.CustomIconPath,
                        Case = path => path is null
                            ? Empty.Widget
                            : new RepoIconImage { Path = path, Size = 16 },
                    },
                ],
            },
        };
    }
}
