using Zdev.Git;
using GitBench.Localization;

namespace GitBench.Controls.Dialogs;

/// <summary>
/// The localized message a ref-name field shows for <see cref="RefNameRules"/>' verdict.
/// <paramref name="noun"/> is the localized ref kind named in the message ("Branch", "Tag").
/// </summary>
internal static class RefNameMessages
{
    public static FieldStatus? Validate(string name, Strings s, string noun) => RefNameRules.Check(name) switch
    {
        RefNameViolation.Spaces => new FieldStatus(FieldSeverity.Error, s.RefnameNoSpaces(noun)),
        RefNameViolation.LeadingDash => new FieldStatus(FieldSeverity.Error, s.RefnameNoLeadingDash(noun)),
        RefNameViolation.DoubleDot => new FieldStatus(FieldSeverity.Error, s.RefnameNoDoubleDot(noun)),
        // A trailing slash means the path isn't finished yet (e.g. a folder-prefilled name);
        // treat it as incomplete/neutral. IsValid still rejects it, so submit stays gated.
        RefNameViolation.TrailingSlash => null,
        _ => null,
    };
}
