using GitBench.Controls;
using GitBench.Features.LocalChanges;
using GitBench.Features.Worktrees;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Repos;

// Shared visual layout for a RepoBar row: indent, expansion chevron, kind glyph, name, and status
// dot, all driven by the surrounding node view model. The primary and nested variants supply the
// glyph, height, and glyph size; the hover flag is owned by the variant's state and drives the accent.
internal sealed record RepoRowShell : Widget
{
    public required string Glyph { get; init; }
    public required float RowHeight { get; init; }
    public required float GlyphSize { get; init; }
    public required IReadable<bool> Hovered { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var vm = ctx.Require<RepoNodeViewModel>();
        var leftPad = RepoBar.RowPaddingLeft + (int)TreeMetrics.IndentLevel * vm.Depth;

        return new Box
        {
            Height = RowHeight,
            Background = Theme.Color(s => vm.IsActive.Value
                ? s.RowSelection.Fill
                : Hovered.Value ? s.RowSelection.FillHover : 0u),
            BorderColor = Theme.BorderColor(s => new BorderColorStyle { Left = s.RowSelection.AccentBar }),
            BorderSize = Prop.Bind(() => new BorderSizeStyle { Left = vm.IsActive.Value ? 2f : 0f }),
            Children =
            [
                new Padding
                {
                    Amount = new PaddingStyle { Left = leftPad, Right = Spacing.Lg },
                    Children =
                    [
                        new Row
                        {
                            Gap = RepoBar.RowIconGap,
                            CrossAxis = CrossAxisAlignment.Center,
                            Children =
                            [
                                new WorktreeChevron().WithController<KbmController>(),
                                new Text
                                {
                                    Value = Glyph,
                                    FontFamily = LucideIcons.FontFamily,
                                    FontSize = GlyphSize,
                                    Width = RepoBar.RowIconWidth,
                                    HAlign = TextAlignment.Center,
                                    VAlign = TextAlignment.Center,
                                    Color = Theme.Color(s => s.RepoBarRow.Icon(vm.Kind, vm.IsActive.Value, vm.IsMissing.Value)),
                                },
                                new Grow
                                {
                                    Child = new Text
                                    {
                                        Value = Prop.Bind<string?>(() => vm.DisplayName.Value),
                                        HAlign = TextAlignment.Start,
                                        VAlign = TextAlignment.Center,
                                        Overflow = TextOverflow.Ellipsis,
                                        Color = Theme.Color(s => s.RepoBarRow.Text(vm.IsActive.Value, vm.IsMissing.Value)),
                                    },
                                },
                                new Box
                                {
                                    Width = 8,
                                    Height = 8,
                                    BorderRadius = BorderRadiusStyle.All(Radius.Sm),
                                    Background = Theme.Color(s => vm.Badge.Value == RepoRowBadge.Error
                                        ? s.RepoBarRow.BadgeError
                                        : s.RepoBarRow.BadgeDirty),
                                    Visible = Prop.Bind(() => vm.Badge.Value != RepoRowBadge.None),
                                },
                            ],
                        },
                    ],
                },
            ],
        };
    }
}
