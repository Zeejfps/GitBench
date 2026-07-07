using GitBench.Controls;
using GitBench.Features.Worktrees;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Repos;

// A RepoBar row: composes the shared TreeRow with the repo kind glyph, name, status dot, and the
// fold chevron, all driven by the surrounding node view model. The primary and nested variants supply
// the glyph, height, and glyph size; the hover flag is owned by the variant's state and drives the
// accent. Registers the row's live rect with the shared selection bar so the active row's fill slides
// here.
internal sealed record RepoRowShell : Widget
{
    public required string Glyph { get; init; }
    public required float RowHeight { get; init; }
    public required float GlyphSize { get; init; }
    public required IReadable<bool> Hovered { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var vm = ctx.Require<RepoNodeViewModel>();
        var selectionBar = ctx.Require<TreeSelectionBar<Guid>>();

        var statusDot = new Box
        {
            Width = 8,
            Height = 8,
            BorderRadius = BorderRadiusStyle.All(Radius.Sm),
            Background = Theme.Color(s => vm.Badge.Value == RepoRowBadge.Error
                ? s.RepoBarRow.BadgeError
                : s.RepoBarRow.BadgeDirty),
            Visible = Prop.Bind(() => vm.Badge.Value != RepoRowBadge.None),
        };

        var nameColor = Theme.Color(s => s.RepoBarRow.Text(vm.IsActive.Value, vm.IsMissing.Value));
        Text NameText() => new()
        {
            Value = Prop.Bind<string?>(() => vm.DisplayName.Value),
            HAlign = TextAlignment.Start,
            VAlign = TextAlignment.Center,
            Overflow = TextOverflow.Ellipsis,
            Color = nameColor,
        };

        var row = new TreeRow
        {
            Depth = vm.Depth,
            RowHeight = RowHeight,
            Guides = Prop.Bind(vm.Guides),
            GlyphSize = GlyphSize,
            Chevron = new WorktreeChevron().WithController<KbmController>(),
            Glyph = Glyph,
            IconColor = Theme.Color(s => s.RepoBarRow.Icon(vm.Kind, vm.IsActive.Value, vm.IsMissing.Value)),
            // A pinned primary shows its slot inline after the name, e.g. "web-frontend (2)", in a
            // smaller muted tone so it reads as a hint, not part of the name. Unpinned/nested rows fall
            // back to the plain growing name so their long-name truncation is untouched.
            NameSlot = new Grow
            {
                Child = new Show
                {
                    When = vm.IsRenaming,
                    Then = () => new RepoRenameField
                    {
                        RepoId = vm.RepoId,
                        InitialName = vm.DisplayName.Value,
                        RowHeight = RowHeight,
                    },
                    Else = () => new Row
                    {
                        CrossAxis = CrossAxisAlignment.Center,
                        Children =
                        [
                            new Grow
                            {
                                Visible = Prop.Bind(() => vm.HotkeyDigit.Value is null),
                                Child = NameText(),
                            },
                            new Row
                            {
                                Visible = Prop.Bind(() => vm.HotkeyDigit.Value is not null),
                                Gap = Spacing.Hair,
                                CrossAxis = CrossAxisAlignment.Center,
                                Children =
                                [
                                    NameText(),
                                    new Text
                                    {
                                        Value = Prop.Bind<string?>(() => vm.HotkeyDigit.Value is { } d ? $"({d})" : string.Empty),
                                        FontSize = FontSize.Caption,
                                        VAlign = TextAlignment.Center,
                                        Color = Theme.Color(s => s.RepoBarRow.Hotkey(vm.IsActive.Value, vm.IsMissing.Value)),
                                    },
                                ],
                            },
                        ],
                    },
                },
            },
            Background = Theme.Color(s => !vm.IsActive.Value && Hovered.Value ? s.RowSelection.FillHover : 0u),
            Trailing = statusDot,
        };

        return row.Use(view => selectionBar.Register(vm.RepoId, () => view.Position));
    }
}
