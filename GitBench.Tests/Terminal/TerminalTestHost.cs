using GitBench.Features.Terminal;
using GitBench.Input;
using GitBench.Localization;
using GitBench.Messages;
using GitBench.Platform;
using GitBench.Theming;
using ZGF.Gui;
using ZGF.Gui.Desktop.Input;
using ZGF.Observable;

namespace GitBench.Tests.Terminal;

/// <summary>What a terminal pane's host registers, in the shapes a test can inspect or ignore.</summary>
internal static class TerminalTestHost
{
    public static void Configure(Context ctx)
    {
        ctx.AddService<IThemeService<ThemeStyles>>(new ThemeService(new State<ThemeMode>(ThemeMode.Dark)));
        ctx.AddService<ILocalizationService>(new LocalizationService(new State<Locale>(Locale.En)));
        ctx.AddService<IClipboard>(new FakeClipboard());
        ctx.AddService<IPlatformShell>(new FakeShell());
        ctx.AddService<IMessageBus>(new MessageBus());
        ctx.AddService<IUiDispatcher>(new QueuedUiDispatcher());
    }

    public static TerminalInputController Controller(
        Context ctx, View view, ITerminalInput terminal, ITerminalCellGeometry cells) =>
        new(
            view,
            ctx.Require<InputSystem>(),
            terminal,
            cells,
            ctx.Require<IClipboard>(),
            ctx.Require<IPlatformShell>(),
            ctx,
            ctx.Require<ILocalizationService>(),
            KeyMap.Defaults,
            ctx.Require<IMessageBus>(),
            ctx.Require<IUiDispatcher>());
}
