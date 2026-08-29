using GitBench.Terminal.Vt.Adapters;

namespace GitBench.Terminal.Vt.Tests;

/// <summary>
/// Every engine the suite runs against, addressed by name.
/// </summary>
/// <remarks>
/// The corpus and golden layers are theories over this list: two engines that emulate a terminal
/// correctly produce the same screen from the same bytes, so a golden that needed a per-engine
/// variant would be recording an implementation rather than a terminal. This is also the one place
/// in the suite that names a concrete engine.
/// </remarks>
public static class TerminalEngines
{
    public const string XtermSharp = "XtermSharp";

    public static IReadOnlyList<string> Names { get; } = [XtermSharp];

    public static IEnumerable<object[]> All() => Names.Select(name => new object[] { name });

    /// <summary>The depth every replay runs at, so a golden's history is the same for every engine.</summary>
    public const int ScrollbackLines = 1000;

    public static ITerminalEngine Create(string name, TerminalSize size) =>
        Create(name, new TerminalSetup(size, ScrollbackLines));

    public static ITerminalEngine Create(string name, TerminalSetup setup) => name switch
    {
        XtermSharp => new XtermSharpEngine(setup),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "No engine is registered under this name."),
    };
}

/// <summary>
/// The engine the focused behavioural specs run against, at whatever geometry the case needs.
/// </summary>
public static class EngineUnderTest
{
    public static ITerminalEngine Create(int columns = 20, int rows = 6) =>
        Create(new TerminalSize(columns, rows));

    public static ITerminalEngine Create(TerminalSize size) =>
        TerminalEngines.Create(TerminalEngines.XtermSharp, size);

    /// <summary>At a scrollback depth of the case's own choosing, for the cases that are about the
    /// depth — filling a shallow history is how a test reaches the behaviour of a full one.</summary>
    public static ITerminalEngine Create(TerminalSetup setup) =>
        TerminalEngines.Create(TerminalEngines.XtermSharp, setup);
}
