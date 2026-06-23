using GitBench.Controls.Dialogs;
using GitBench.Localization;

namespace GitBench.Git;

/// <summary>
/// A lightweight subset of git's refname rules (see <c>git check-ref-format</c>), shared by the
/// branch- and tag-name dialogs. Enough to catch the obvious typos live while a dialog is open;
/// the git invocation on submit is still the source of truth for the full rule set.
///
/// <paramref name="noun"/> is the localized ref kind named in the message ("Branch", "Tag"). Empty
/// input is reported as neutral (<c>null</c>) rather than as an error — callers gate their submit
/// button on emptiness (and dialog-specific conditions like "same as current name") separately, and
/// optional name fields simply stay quiet when left blank. <see cref="IsValid"/> is the
/// locale-independent gate; <see cref="Validate"/> produces the localized message for display.
/// </summary>
internal static class RefNameRules
{
    private enum Violation { None, Spaces, LeadingDash, DoubleDot, TrailingSlash }

    public static bool IsValid(string name) => Check(name) == Violation.None;

    public static FieldStatus? Validate(string name, Strings s, string noun) => Check(name) switch
    {
        Violation.Spaces => new FieldStatus(FieldSeverity.Error, s.RefnameNoSpaces(noun)),
        Violation.LeadingDash => new FieldStatus(FieldSeverity.Error, s.RefnameNoLeadingDash(noun)),
        Violation.DoubleDot => new FieldStatus(FieldSeverity.Error, s.RefnameNoDoubleDot(noun)),
        Violation.TrailingSlash => new FieldStatus(FieldSeverity.Error, s.RefnameNoTrailingSlash(noun)),
        _ => null,
    };

    private static Violation Check(string name)
    {
        if (name.Length == 0) return Violation.None;

        foreach (var c in name)
            if (char.IsWhiteSpace(c))
                return Violation.Spaces;

        if (name[0] == '-') return Violation.LeadingDash;
        if (name.Contains("..")) return Violation.DoubleDot;
        if (name[^1] == '/') return Violation.TrailingSlash;

        return Violation.None;
    }
}
