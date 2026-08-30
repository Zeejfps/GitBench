namespace GitBench.Features.Terminal;

/// <summary>
/// Text on its way to the system clipboard, already made safe to put there.
/// </summary>
/// <remarks>
/// <para>
/// A value rather than a plain string because the two sources are not equally trusted and the
/// difference is invisible at a call site that takes a string. What the user highlighted came out of
/// this application's own grid; what a program sent through OSC 52 came off a pseudo-terminal, and
/// it can carry control characters that stage something the user would then paste somewhere else.
/// </para>
/// <para>
/// The constructors are the only way in, so a raw OSC 52 payload cannot reach the clipboard without
/// passing the one that sanitises it.
/// </para>
/// </remarks>
internal readonly record struct ClipboardText
{
    public const int MaxCharacters = 1 << 20;

    ClipboardText(string value) => Value = value;

    public string Value { get; }

    public bool IsEmpty => string.IsNullOrEmpty(Value);

    /// <summary>Text the user highlighted on the screen. Already this application's own.</summary>
    public static ClipboardText FromSelection(string text) => new(text);

    /// <summary>
    /// Text a program asked to be put on the clipboard. Control characters are dropped and the
    /// length is capped; null when nothing survives that.
    /// </summary>
    /// <remarks>
    /// Tab and the newlines stay, because a program copying a block of text legitimately sends them.
    /// Everything else in C0 and C1 is dropped: a clipboard is pasted into other terminals, and an
    /// escape sequence or a carriage return that hides what follows it is the reason this filter
    /// exists at all.
    /// </remarks>
    public static ClipboardText? FromProgram(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;

        var length = Math.Min(text.Length, MaxCharacters);
        var kept = new System.Text.StringBuilder(length);

        for (var i = 0; i < length; i++)
        {
            var character = text[i];

            if (character is '\t' or '\n') { kept.Append(character); continue; }
            if (character < 0x20 || character == 0x7F) continue;
            if (character is >= (char)0x80 and <= (char)0x9F) continue;

            kept.Append(character);
        }

        return kept.Length == 0 ? null : new ClipboardText(kept.ToString());
    }
}
