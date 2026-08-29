namespace GitBench.Terminal.Vt;

/// <summary>How the cursor is drawn.</summary>
public enum CursorShape : byte { Block = 0, Underline = 1, Bar = 2 }

/// <summary>Which mouse reports the program has asked for.</summary>
public enum MouseTracking : byte { Off = 0, X10 = 1, Normal = 2, ButtonEvent = 3, AnyEvent = 4 }

/// <summary>How a mouse report is encoded on the wire.</summary>
public enum MouseEncoding : byte { X10 = 0, Utf8 = 1, Sgr = 2, Urxvt = 3 }

/// <summary>Where the cursor is and how it looks.</summary>
public readonly record struct TerminalCursor(
    int Column,
    int Row,
    bool Visible,
    CursorShape Shape,
    bool Blinking);

/// <summary>
/// The terminal modes a caller outside the engine has to act on.
/// </summary>
/// <remarks>
/// Enums where the settings are alternatives that supersede one another, independent booleans where
/// they genuinely are independent. <see cref="SynchronizedOutput"/> is reported, never honoured: the
/// engine keeps applying bytes and the renderer decides to hold the previous image, because an
/// engine that buffered the frame would make the grid lie about what it has received and would
/// strand the screen if a program never sent the closing sequence.
/// </remarks>
public readonly record struct TerminalModes(
    bool ApplicationCursorKeys,
    bool ApplicationKeypad,
    bool AutoWrap,
    bool AlternateScreen,
    bool AlternateScroll,
    bool BracketedPaste,
    bool FocusReporting,
    bool SynchronizedOutput,
    MouseTracking MouseTracking,
    MouseEncoding MouseEncoding,
    int KeyboardProtocolFlags,
    int ModifyOtherKeys);

/// <summary>
/// Everything observable about the terminal that is not a cell, as one consistent value.
/// </summary>
/// <remarks>
/// One snapshot rather than a dozen properties on the engine, because the callers read these in
/// groups: the key encoder needs application-cursor and the keyboard flags together, the renderer
/// needs alt-screen and cursor visibility together. Reading them one at a time is the torn-view
/// problem this codebase already carries a scar from.
/// </remarks>
public readonly record struct TerminalState(
    TerminalCursor Cursor,
    TerminalModes Modes,
    string Title,
    string IconTitle);
