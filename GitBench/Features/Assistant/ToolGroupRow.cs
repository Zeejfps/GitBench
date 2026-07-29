using GitBench.Controls;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Assistant;

/// <summary>
/// A run of tool calls as one transcript entry: the single line it always was while the run is one
/// call, and a summary the reader can open once it is more.
/// </summary>
internal sealed record ToolGroupRow : Widget
{
    public required AssistantToolGroup Group { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var group = Group;

        return new Switch<bool>
        {
            Value = group.IsSingle,
            Case = single => single
                ? new ToolCallRow { Row = group.Calls[0] }
                : new ToolGroupSummary { Group = group },
        };
    }
}

/// <summary>The folded run: how many calls it made, whether any of them failed, and the calls
/// themselves once it is opened.</summary>
internal sealed record ToolGroupSummary : Widget
{
    public const string ToggleId = "assistant-tool-group";

    public required AssistantToolGroup Group { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var group = Group;
        var theme = ctx.Theme();
        var loc = ctx.Localization();

        return new Column
        {
            Gap = Spacing.Hair,
            CrossAxis = CrossAxisAlignment.Stretch,
            Children =
            [
                new ButtonWidget
                {
                    Id = ToggleId,
                    // A failure outranks hover: the one thing this line must never do is read calm
                    // over a call that did not work.
                    Style = ButtonStyle.Bare(state => Prop.Bind(() =>
                    {
                        var styles = theme.Styles.Value;
                        if (group.FailedCount.Value > 0) return styles.Status.DangerText;
                        return state.Hovered.Value ? styles.Palette.TextPrimary : styles.Palette.TextMuted;
                    })),
                    Command = group.Toggle,
                    Children =
                    [
                        new ButtonIcon
                        {
                            Value = Prop.Bind<string?>(() => group.FailedCount.Value > 0
                                ? LucideIcons.TriangleAlert
                                : LucideIcons.SquareTerminal),
                            FontSize = FontSize.Caption,
                        },
                        new Text
                        {
                            Value = Prop.Bind<string?>(() => Summary(loc.Strings.Value, group)),
                            FontSize = FontSize.Caption,
                            VAlign = TextAlignment.Center,
                            Color = Foreground.Color,
                        },
                        new ButtonIcon
                        {
                            Value = Prop.Bind<string?>(() => group.IsExpanded.Value
                                ? LucideIcons.ChevronUp
                                : LucideIcons.ChevronDown),
                            FontSize = FontSize.Caption,
                        },
                    ],
                }
                .WithController<KbmController>(),
                new Show
                {
                    When = group.IsExpanded,
                    Then = () => new Padding
                    {
                        Amount = new PaddingStyle { Left = TranscriptGlyph.Box + Spacing.Sm },
                        Children =
                        [
                            new Each<AssistantRow>
                            {
                                Items = group.Calls,
                                Template = new ToolGroupCall(),
                                Gap = Spacing.Hair,
                                CrossAxis = CrossAxisAlignment.Stretch,
                            },
                        ],
                    },
                },
            ],
        };
    }

    private static string Summary(Strings strings, AssistantToolGroup group)
    {
        var count = group.Calls.Count;
        var failed = group.FailedCount.Value;
        var label = group.IsRunning.Value
            ? strings.AssistantToolGroupRunning(count)
            : strings.AssistantToolGroup(count);
        return failed == 0 ? label : label + " · " + strings.AssistantToolGroupFailed(failed);
    }
}

/// <summary>One call inside an opened run. Reads the row from the list's scope, like every other
/// <see cref="Each{T}"/> template.</summary>
internal sealed record ToolGroupCall : Widget
{
    protected override IWidget Build(Context ctx) => new ToolCallRow { Row = ctx.Require<AssistantRow>() };
}
