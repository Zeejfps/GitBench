using System.Text.Json.Serialization;

namespace GitBench.Features.LocalChanges;

/// <summary>
/// How the Changes tab presents the working tree. Both layouts drive the same staging state and share
/// the commit bar — this is a presentation choice, not a mode.
/// </summary>
/// <remarks>The wire names are frozen at the members' original names so preference files written
/// before the rename still parse.</remarks>
public enum WorkingChangesLayout
{
    /// <summary>Every changed file's diff stacked in one scroll, GitHub-PR style; a file's checkbox is
    /// its staged state.</summary>
    [JsonStringEnumMemberName("Review")]
    Diff,

    /// <summary>Unstaged / staged file lists over a diff pane for the selected file.</summary>
    [JsonStringEnumMemberName("List")]
    Files,
}
