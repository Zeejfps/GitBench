namespace GitGui;

/// <summary>
/// A lightweight subset of git's refname rules (see <c>git check-ref-format</c>), enough to
/// catch the obvious typos live while a branch dialog is open. The git invocation on submit is
/// still the source of truth for the full rule set; this just gives early, field-local feedback
/// under the name input.
///
/// Empty input is reported as neutral (<c>null</c>) rather than as an error — callers gate their
/// submit button on emptiness (and on dialog-specific conditions like "same as current name")
/// separately, so the field stays quiet until the user types something actually invalid.
/// </summary>
internal static class BranchNameRules
{
    public static FieldStatus? Validate(string name)
    {
        if (name.Length == 0) return null;

        foreach (var c in name)
        {
            if (char.IsWhiteSpace(c))
                return new FieldStatus(FieldSeverity.Error, "Branch names can’t contain spaces.");
        }

        if (name[0] == '-')
            return new FieldStatus(FieldSeverity.Error, "Branch names can’t start with “-”.");
        if (name.Contains(".."))
            return new FieldStatus(FieldSeverity.Error, "Branch names can’t contain “..”.");
        if (name[^1] == '/')
            return new FieldStatus(FieldSeverity.Error, "Branch names can’t end with “/”.");

        return null;
    }
}
