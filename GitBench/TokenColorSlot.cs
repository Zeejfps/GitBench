namespace GitGui;

/// <summary>
/// The small, curated set of foreground color slots a TextMate scope can resolve to. These
/// are theme-agnostic intents; each is bound to a concrete palette color per light/dark theme
/// in <see cref="DiffContentStyles"/>. <see cref="Default"/> means "no special color" — the
/// token renders in the line's ordinary text color, identical to plain (un-highlighted) diff.
/// </summary>
internal enum TokenColorSlot
{
    Default,
    Keyword,
    String,
    Comment,
    Number,
    Type,
    Function,
    Variable,
    Operator,
    Punctuation,
    Constant,
}

/// <summary>
/// A colored run inside a single line's text, in tab-expanded column coordinates: <see
/// cref="Start"/> is the 0-based column, <see cref="Length"/> the character count, both
/// measured against the same tab-expanded string the renderer draws.
/// </summary>
internal readonly record struct TokenSpan(int Start, int Length, TokenColorSlot Slot);
