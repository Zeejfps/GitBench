using GitBench.Controls;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Assistant;

/// <summary>
/// Where the next message is written: a field bound to the draft that grows with what is typed, and
/// the action button beside it. Enter sends, Shift+Enter breaks the line.
/// </summary>
internal sealed record AssistantComposer : Widget
{
    public const string InputId = "assistant-input";

    // One line at rest, and past roughly five the field stops growing and scrolls — the panel is a
    // fixed height, so anything more would be taken out of the transcript.
    private const float FieldMinHeight = 0f;
    internal const float FieldMaxHeight = 120f;

    protected override IWidget Build(Context ctx)
    {
        var vm = ctx.Require<AssistantViewModel>();
        var loc = ctx.Localization();

        var field = new GrowingDescriptionField(ctx, FieldMinHeight, FieldMaxHeight)
        {
            Id = InputId,
            OnSubmit = () => vm.Send.Execute(),
        };
        field.Bind(loc.Strings, s => field.PlaceholderText = s.AssistantPlaceholder);
        field.BindTwoWay(vm.Draft, vm.SetDraft);

        return new Box
        {
            BorderSize = new BorderSizeStyle { Top = 1 },
            BorderColor = Theme.BorderColor(s => new BorderColorStyle { Top = s.Palette.Border }),
            Children =
            [
                new Padding
                {
                    Amount = PaddingStyle.All(Spacing.Md),
                    Children =
                    [
                        new Row
                        {
                            Gap = Spacing.Sm,
                            // End, not Center: the button stays on the last line as the field grows
                            // upward, instead of drifting to the middle of a tall box.
                            CrossAxis = CrossAxisAlignment.End,
                            Children = [new Grow { Child = new Raw { View = field } }, new AssistantSendButton()],
                        },
                    ],
                },
            ],
        };
    }
}

/// <summary>
/// The composer's single action: send while idle, stop the turn while one runs. One button rather
/// than two, because sending during a turn is not offered — a send does not queue.
/// </summary>
internal sealed record AssistantSendButton : Widget
{
    public const string SendId = "assistant-send";
    public const string StopId = "assistant-stop";

    protected override IWidget Build(Context ctx)
    {
        var vm = ctx.Require<AssistantViewModel>();

        return new Switch<bool>
        {
            Value = vm.IsBusy,
            Case = busy => busy
                ? new ButtonWidget
                {
                    Id = StopId,
                    Style = ButtonStyle.Outline(static s => s.Status.DangerBar),
                    ContentInset = ButtonStyle.Outline(static s => s.Status.DangerBar).IconOnlyInset,
                    Command = vm.Stop,
                    Children = [new ButtonIcon { Value = LucideIcons.X }],
                }
                .WithTooltip(L.T(s => s.AssistantStop))
                .WithController<KbmController>()
                : new ButtonWidget
                {
                    Id = SendId,
                    Style = ButtonStyle.Filled(static s => s.Palette.Accent),
                    ContentInset = ButtonStyle.Filled(static s => s.Palette.Accent).IconOnlyInset,
                    Command = vm.Send,
                    Children = [new ButtonIcon { Value = LucideIcons.Push }],
                }
                .WithTooltip(L.T(s => s.AssistantSend))
                .WithController<KbmController>(),
        };
    }
}
