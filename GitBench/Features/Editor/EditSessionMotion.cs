namespace GitBench.Features.Editor;

/// <summary>Whether a motion drags the selection's far end with it or drops the selection where it
/// lands.</summary>
internal enum SelectionIntent
{
    Move,
    Extend,
}

/// <summary>Which way a motion or a deletion runs. Backward is toward the start of the document.</summary>
internal enum MoveDirection
{
    Backward,
    Forward,
}

/// <summary>How much text one step covers. <see cref="Cluster"/> is a grapheme cluster, not a char.</summary>
internal enum TextUnit
{
    Cluster,
    Word,
}

/// <summary>Which end of a line Home and End mean. <see cref="SmartStart"/> is Home's two-stop
/// behaviour: first non-whitespace character, then column zero.</summary>
internal enum LineEdge
{
    SmartStart,
    End,
}

internal enum DocumentEdge
{
    Start,
    End,
}
