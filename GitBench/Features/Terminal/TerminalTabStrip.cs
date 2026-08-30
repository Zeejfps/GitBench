using GitBench.Controls;
using GitBench.Localization;
using GitBench.Messages;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Terminal;

/// <summary>
/// The strip above the terminal grid: one tab per terminal of the active repository, and a
/// <c>+</c> on the trailing edge that opens another.
/// </summary>
/// <remarks>
/// Not drawn until a terminal has been started: a repository whose terminal has never been used is
/// showing the offer to start one, and a strip naming a shell that does not exist would be chrome
/// over an empty screen. From the first start on it stays, including for a shell that has since
/// exited — the tab is how that screen is still reachable.
/// </remarks>
internal sealed record TerminalTabStrip : Widget
{
    /// <summary>The strip's id, so a test can ask whether it is on screen at all.</summary>
    public const string StripId = "terminal-tab-strip";

    /// <summary>The <c>+</c>'s id, so a test can press the thing a user presses.</summary>
    public const string NewTabButtonId = "terminal-new-tab";

    const int ButtonSize = 24;

    // Centres the button in the strip by inset rather than by a Center, whose intrinsic width a
    // trailing slot beside a Grow has nothing to lay out against.
    const int ButtonInset = ((int)TabStrip.Height - ButtonSize) / 2;

    public required TerminalTabs Tabs { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var tabs = Tabs;

        return new TabStrip
        {
            Id = StripId,
            // A plane above the grid, so the active tab — which wears the grid's own colour —
            // reads as a notch cut through to it.
            Background = Theme.Color(s => s.Palette.SurfaceRaised),
            Tabs =
            [
                Each.Of(tabs.Terminals, new TerminalTab { Tabs = tabs }, axis: Axis.Horizontal)
                    with { CrossAxis = CrossAxisAlignment.Stretch },
                NewTabButton(tabs),
            ],
        };
    }

    /// <summary>
    /// The <c>+</c>, immediately after the last tab rather than pinned to the strip's far edge.
    /// </summary>
    /// <remarks>
    /// It is one of the tabs' own row, so it sits where the reader's eye already is and pans with
    /// them when they overflow — which is what every terminal does with it. Pinned to the trailing
    /// edge it stayed reachable at any width, but with three short tabs in a wide pane it read as
    /// an unrelated toolbar button stranded on the other side of the header.
    /// </remarks>
    static IWidget NewTabButton(TerminalTabs tabs) => new Padding
    {
        Amount = new PaddingStyle
        {
            Left = Spacing.Xs,
            Right = Spacing.Xs,
            Top = ButtonInset,
            Bottom = ButtonInset,
        },
        Children =
        [
            new IconButtonWidget
            {
                Id = NewTabButtonId,
                Icon = LucideIcons.Plus,
                IconSize = 15f,
                Width = ButtonSize,
                Height = ButtonSize,
                // Opens and starts, in that order and in one gesture: asking for another terminal is
                // asking for another shell, and the start gate exists for the first one only because
                // a repository is given a terminal it never asked for. The spawn waits for the new
                // grid's first viewport report, which is the same path the gate's own click takes.
                Command = new Command(() => tabs.Open().Start()),
                Surface = s => Theme.Color(t => t.HeaderActionButton.Surface(s)),
                Foreground = s => Theme.Color(t => t.HeaderActionButton.Icon(s)),
            }
                .WithTooltip(L.T(s => s.TerminalNewTab))
                .WithController<KbmController>(),
        ],
    };
}

/// <summary>
/// One terminal's tab. Resolves its <see cref="TerminalInstance"/> from the list scope.
/// </summary>
/// <remarks>
/// Every tab closes, the last one included: closing it ends its shell and hands the repository back
/// the unstarted terminal it began with, which takes the strip with it and puts the offer to start
/// a shell back on screen.
/// </remarks>
internal sealed record TerminalTab : Widget
{
    public required TerminalTabs Tabs { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var terminal = ctx.Require<TerminalInstance>();
        var tabs = Tabs;
        var loc = ctx.Localization();
        var bus = ctx.Require<IMessageBus>();

        return new TabChrome
        {
            // Tracked: the label follows the running command's title, and the trailing index follows
            // whichever siblings currently share it.
            Label = Prop.Bind(() => Label(loc.Strings.Value, tabs.Terminals, terminal)),
            ContentBackground = static s => s.Terminal.DefaultBackground,
            IsActive = () => ReferenceEquals(tabs.Active.Value, terminal),
            OnActivate = () => tabs.Activate(terminal),
            OnClose = () => RequestClose(bus, tabs, terminal),
        };
    }

    static string Label(Strings strings, IReadOnlyList<TerminalInstance> terminals, TerminalInstance terminal)
    {
        var label = TerminalTabLabels.For(terminals, terminal);
        return label.Index is { } index ? strings.TerminalTabIndexed(label.Text, index) : label.Text;
    }

    /// <summary>
    /// Closes the tab, asking first when there is a shell to lose.
    /// </summary>
    /// <remarks>
    /// A close request is not a close: the dialog is answered later, and until it is the tab stays
    /// exactly where it was — still active if it was active, still taking output, because the shell
    /// is still running. <see cref="TerminalTabs.Close"/> is by identity for the same reason.
    /// </remarks>
    static void RequestClose(IMessageBus bus, TerminalTabs tabs, TerminalInstance terminal)
    {
        if (!terminal.HasLiveShell)
        {
            tabs.Close(terminal);
            return;
        }

        var name = TerminalTabLabels.NameOf(terminal);
        bus.Broadcast(new ShowDialogMessage(onClose => new ConfirmCloseTerminalDialog
        {
            Terminal = name,
            OnClose = onClose,
            OnConfirm = () => tabs.Close(terminal),
        }));
    }
}
