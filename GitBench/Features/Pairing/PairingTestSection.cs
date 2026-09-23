using GitBench.Controls;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Pairing;

/// <summary>
/// A test stop's test on the stop card: waiting for the agent to write it, asking how tests run
/// here, running, or red with its output and Undo test — and, when Done found it still red, Done
/// anyway.
/// </summary>
internal sealed record PairingTestSection : Widget
{
    public const string RunId = "pairing-test-run";
    public const string OpenTestId = "pairing-test-open";
    public const string UndoId = "pairing-test-undo";
    public const string CloseRedId = "pairing-done-anyway";

    private const int OutputLines = 14;

    public required PairingStore Store { get; init; }
    public required StopTest? Test { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var store = Store;
        var loc = ctx.Localization();
        if (Test is not { } test) return Caption(L.T(s => s.PairingWaitingForTest), static s => s.Palette.TextSecondary);

        // The test is the agent's code, so it is one click from being read before it is run.
        var header = new ButtonWidget
        {
            Id = OpenTestId,
            Style = ButtonStyle.Bare(_ => Theme.Color(s => s.Palette.TextBody)),
            Command = new Command(store.OpenTest),
            ContentInset = PaddingStyle.All(Spacing.None),
            Children =
            [
                new Text
                {
                    Value = Prop.Bind<string?>(() => loc.Strings.Value.PairingTestFile(test.Path, test.Name)),
                    FontSize = FontSize.Caption,
                    FontFamily = MonoFonts.Regular,
                    Wrap = TextWrap.Wrap,
                    Color = Theme.Color(s => s.Palette.Accent),
                },
            ],
        }.WithController<KbmController>();

        var body = new List<IWidget> { header };
        switch (test.State)
        {
            case TestState.AwaitingRun awaiting:
                body.Add(CommandPrompt(ctx, awaiting.Command, null));
                break;
            case TestState.Unrunnable unrunnable:
                body.Add(CommandPrompt(ctx, store.TestCommandTemplate ?? string.Empty, unrunnable.Reason));
                break;
            case TestState.Running:
                body.Add(Caption(L.T(s => s.PairingRunningTest), static s => s.Palette.TextSecondary));
                break;
            case TestState.Red red:
                body.Add(Caption(
                    red.AfterDone ? L.T(s => s.PairingTestStillRed) : Prop.Bind<string?>(() => loc.Strings.Value.PairingTestRed(test.Name)),
                    static s => s.Status.DangerText));
                body.Add(Output(red.Run.Output));
                var buttons = new List<IWidget>
                {
                    new ButtonWidget
                    {
                        Id = UndoId,
                        Style = ButtonStyle.Outline(static s => s.Palette.TextBody),
                        Command = new Command(() => _ = store.UndoTestAsync()),
                        Children = [new ButtonLabel { Value = L.T(s => s.PairingUndoTest) }],
                    }.WithController<KbmController>(),
                };
                if (red.AfterDone)
                {
                    buttons.Add(new ButtonWidget
                    {
                        Id = CloseRedId,
                        Style = ButtonStyle.Outline(static s => s.Status.DangerText),
                        Command = new Command(() => _ = store.CloseRedAsync()),
                        Children = [new ButtonLabel { Value = L.T(s => s.PairingDoneAnyway) }],
                    }.WithController<KbmController>());
                }

                body.Add(new Row { Gap = Spacing.Sm, CrossAxis = CrossAxisAlignment.Center, Children = [.. buttons] });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(Test), test.State, "Unknown test state.");
        }

        return new Column
        {
            Gap = Spacing.Sm,
            CrossAxis = CrossAxisAlignment.Stretch,
            Children = [.. body],
        };
    }

    private IWidget CommandPrompt(Context ctx, string start, string? problem)
    {
        var store = Store;
        var command = new State<string>(start);
        var canRun = new Derived<bool>(() => command.Value.Trim().Length > 0);
        var children = new List<IWidget>();
        if (problem is not null) children.Add(Caption(problem, static s => s.Status.DangerText));
        children.Add(new LabeledInput
        {
            Label = ctx.Localization().Strings.Value.PairingTestCommand,
            Value = command,
            Hint = ctx.Localization().Strings.Value.PairingTestCommandHint(TestCommand.Placeholder),
            Placeholder = "dotnet test --filter {test}",
        });
        children.Add(new Row
        {
            Children =
            [
                new ButtonWidget
                {
                    Id = RunId,
                    Style = ButtonStyle.Filled(static s => s.Palette.Accent),
                    Command = new Command(() => store.RunTest(command.Value), canRun),
                    Children = [new ButtonLabel { Value = L.T(s => s.PairingRunTest) }],
                }.WithController<KbmController>(),
            ],
        });
        return new Column { Gap = Spacing.Sm, CrossAxis = CrossAxisAlignment.Stretch, Children = [.. children] };
    }

    // The end of the output, where a runner puts the failure.
    private static IWidget Output(string output)
    {
        var lines = output.TrimEnd().Split('\n');
        var tail = string.Join('\n', lines.Skip(Math.Max(0, lines.Length - OutputLines))).TrimEnd();
        return new Box
        {
            Background = Theme.Color(s => s.Palette.Surface),
            BorderRadius = BorderRadiusStyle.All(Radius.Sm),
            Children =
            [
                new Padding
                {
                    Amount = PaddingStyle.All(Spacing.Sm),
                    Children =
                    [
                        new Text
                        {
                            Value = tail,
                            FontSize = FontSize.Caption,
                            FontFamily = MonoFonts.Regular,
                            Wrap = TextWrap.Wrap,
                            Color = Theme.Color(s => s.Palette.TextBody),
                        },
                    ],
                },
            ],
        };
    }

    private static IWidget Caption(Prop<string?> text, Func<GitBench.Theming.ThemeStyles, uint> color) => new Text
    {
        Value = text,
        FontSize = FontSize.Caption,
        Wrap = TextWrap.Wrap,
        Color = Theme.Color(color),
    };
}
