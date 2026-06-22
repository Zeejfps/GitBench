using GitBench.Features.Commits;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Widgets;

namespace GitBench.Features.LocalChanges;

/// <summary>Square colored badge containing the single-letter status glyph for a file change.</summary>
internal sealed record FileStatusBadge : Widget
{
    public required FileChangeStatus Status { get; init; }

    protected override IWidget Build(Context ctx) =>
        new Box
        {
            Width = FileChangesUI.BadgeSize,
            Height = FileChangesUI.BadgeSize,
            BorderRadius = BorderRadiusStyle.All(3),
            Background = Theme.Color(s => s.FileChangeRow.StatusColor(Status)),
            Children =
            [
                new Text
                {
                    Value = FileChangeFormatting.StatusGlyph(Status),
                    FontSize = 11f,
                    HAlign = TextAlignment.Center,
                    VAlign = TextAlignment.Center,
                    Color = Theme.Color(s => s.FileChangeRow.BadgeText),
                },
            ],
        };
}
