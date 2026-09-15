using GitBench.App;
using GitBench.Controls;
using GitBench.Features.Repos;
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
/// The one control that makes a terminal: pinned on the trailing edge of the content panel's tab
/// strip, where it is reachable however many tabs are open.
/// </summary>
/// <remarks>
/// A terminal glyph over a plus rather than a bare plus, because the strip it sits on is not the
/// terminal's own any more — a plus at the end of a run that also holds files and views would not
/// say what it makes.
/// </remarks>
internal sealed record NewTerminalButton : Widget
{
    /// <summary>The button's id, so a test can press the thing a user presses.</summary>
    public const string NewTabButtonId = "terminal-new-tab";

    const int ButtonHeight = 24;
    const int ButtonWidth = 34;
    const float GlyphSize = 15f;
    const float PlusSize = 9f;

    // Centres the button in the strip by inset rather than by a Center, whose intrinsic width a
    // trailing slot beside a Grow has nothing to lay out against.
    const int ButtonInset = ((int)TabStrip.StripHeight - ButtonHeight) / 2;

    /// <summary>Put the terminal that was made on screen. The strip's, not this control's: what
    /// "showing" means belongs to the panel these tabs are for.</summary>
    public required Action<TerminalInstance> OnShow { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var terminals = ctx.Require<ITerminalSessionStore>();

        return new Padding
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
                new NewTerminalGlyphButton
                {
                    Id = NewTabButtonId,
                    Width = ButtonWidth,
                    Height = ButtonHeight,
                    GlyphSize = GlyphSize,
                    PlusSize = PlusSize,
                    // Makes and starts, in that order and in one gesture: asking for a terminal is
                    // asking for a shell. The spawn waits for the new grid's first viewport report.
                    Command = new Command(() =>
                    {
                        if (terminals.Tabs.Value is not { } tabs) return;
                        OnShow(tabs.StartNew());
                    }),
                }
                    .WithTooltip(L.T(s => s.TerminalNewTab))
                    .WithController<KbmController>(),
            ],
        };
    }
}

/// <summary>The button's two glyphs on one themed surface, so the whole control hovers and presses
/// as one rather than only the half the pointer happens to be over.</summary>
internal sealed record NewTerminalGlyphButton : Widget<ButtonState>
{
    public ICommand? Command { get; init; }
    public required float GlyphSize { get; init; }
    public required float PlusSize { get; init; }

    protected override ButtonState CreateState(Context ctx) => new(Command);

    protected override IWidget Build(Context ctx, ButtonState state) => new Box
    {
        BorderRadius = BorderRadiusStyle.All(Radius.Sm),
        Background = Theme.Color(t => t.HeaderActionButton.Surface(state)),
        Children =
        [
            new Row
            {
                Gap = Spacing.Hair,
                MainAxis = MainAxisAlignment.Center,
                CrossAxis = CrossAxisAlignment.Center,
                Children =
                [
                    Glyph(state, LucideIcons.SquareTerminal, GlyphSize),
                    Glyph(state, LucideIcons.Plus, PlusSize),
                ],
            },
        ],
    };

    private static IWidget Glyph(ButtonState state, string icon, float size) => new Text
    {
        Value = icon,
        FontFamily = LucideIcons.FontFamily,
        FontSize = size,
        HAlign = TextAlignment.Center,
        VAlign = TextAlignment.Center,
        Color = Theme.Color(t => t.HeaderActionButton.Icon(state)),
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

    /// <summary>
    /// Whether the content panel is showing a terminal at all. A terminal tab shares its strip with
    /// the views and the open files, so being the active terminal is only half of being the tab the
    /// reader is looking at.
    /// </summary>
    public required Func<bool> PaneShowing { get; init; }

    /// <summary>Put this terminal's grid on screen. The panel's business, not this tab's.</summary>
    public required Action OnShow { get; init; }

    /// <summary>The terminal this tab is of. Handed in rather than resolved from the list scope,
    /// because the run these sit in holds tabs of more than one kind and only its own wrapper is in
    /// scope.</summary>
    public required TerminalInstance Instance { get; init; }

    /// <summary>How this tab is dragged to a new place in the run.</summary>
    public required ITabDrag Drag { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var terminal = Instance;
        var tabs = Tabs;
        var loc = ctx.Localization();
        var bus = ctx.Require<IMessageBus>();

        return new TabChrome
        {
            Drag = Drag,
            Leading = new TabIcon
            {
                Glyph = LucideIcons.SquareTerminal,
                Color = Theme.Color(s => IsShowing() ? s.Palette.TextPrimary : s.Palette.TextMuted),
            },
            // Tracked: the label follows the running command's title, and the trailing index follows
            // whichever siblings currently share it.
            Label = Prop.Bind(() => Label(loc.Strings.Value, tabs.Terminals, terminal)),
            ContentBackground = static s => s.Terminal.DefaultBackground,
            IsActive = IsShowing,
            OnActivate = () =>
            {
                tabs.Activate(terminal);
                OnShow();
            },
            OnClose = () => RequestClose(bus, tabs, terminal),
            OnContextMenu = point => RepoBarContextMenu.Show(
                ctx, point, MenuItems(loc.Strings.Value, bus, tabs, terminal)),
        };

        bool IsShowing() => PaneShowing() && ReferenceEquals(tabs.Active.Value, terminal);
    }

    /// <summary>
    /// What a tab offers: a name of the reader's own, and the close the X already does.
    /// </summary>
    /// <remarks>
    /// Built on each opening rather than once, so the reset item is offered only while there is a
    /// given name to drop and the labels are in whatever language is current.
    /// </remarks>
    static IReadOnlyList<RepoBarContextMenu.Item> MenuItems(
        Strings s, IMessageBus bus, TerminalTabs tabs, TerminalInstance terminal)
    {
        var items = new List<RepoBarContextMenu.Item>
        {
            new(s.TerminalTabRename, () => RequestRename(bus, terminal), LucideIcons.PencilLine),
        };

        if (terminal.GivenName.Value != null)
            items.Add(new RepoBarContextMenu.Item(
                s.TerminalTabResetName, () => terminal.Rename(null), LucideIcons.Undo));

        items.Add(RepoBarContextMenu.Separator);
        items.Add(new RepoBarContextMenu.Item(
            s.CommonClose, () => RequestClose(bus, tabs, terminal), LucideIcons.X));
        return items;
    }

    /// <summary>
    /// Asks for a name, and gives the one that comes back to the terminal rather than to the tab: a
    /// dialog outlives the widget that opened it — a rename answered after the pane has unmounted or
    /// the repository has changed still names the terminal the reader was pointing at.
    /// </summary>
    static void RequestRename(IMessageBus bus, TerminalInstance terminal) =>
        bus.Broadcast(new ShowDialogMessage(onClose => new RenameTerminalDialog
        {
            CurrentName = TerminalTabLabels.NameOf(terminal),
            OnClose = onClose,
            OnRename = terminal.Rename,
        }));

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
