namespace GitGui;

/// <summary>
/// A lightweight subset of git's refname rules (see <c>git check-ref-format</c>), shared by the
/// branch- and tag-name dialogs. Enough to catch the obvious typos live while a dialog is open;
/// the git invocation on submit is still the source of truth for the full rule set.
///
/// <paramref name="noun"/> is the ref kind named in the message ("Branch", "Tag"), e.g.
/// "Branch names can’t contain spaces." Empty input is reported as neutral (<c>null</c>) rather
/// than as an error — callers gate their submit button on emptiness (and dialog-specific
/// conditions like "same as current name") separately, and optional name fields simply stay
/// quiet when left blank.
/// </summary>
internal static class RefNameRules
{
    public static FieldStatus? Validate(string name, string noun)
    {
        if (name.Length == 0) return null;

        foreach (var c in name)
        {
            if (char.IsWhiteSpace(c))
                return new FieldStatus(FieldSeverity.Error, $"{noun} names can’t contain spaces.");
        }

        if (name[0] == '-')
            return new FieldStatus(FieldSeverity.Error, $"{noun} names can’t start with “-”.");
        if (name.Contains(".."))
            return new FieldStatus(FieldSeverity.Error, $"{noun} names can’t contain “..”.");
        if (name[^1] == '/')
            return new FieldStatus(FieldSeverity.Error, $"{noun} names can’t end with “/”.");

        return null;
    }
}
