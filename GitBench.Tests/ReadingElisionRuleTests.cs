using GitBench.Features.Diff.Reading;
using Xunit;

namespace GitBench.Tests;

// The elision rule is the only thing standing between a model-chosen replacement and invented
// source. Everything it lets through must be the original with spans deleted; anything that adds,
// reorders or alters a character has to be rejected, or an abridged line can claim something the
// code never said.
public class ReadingElisionRuleTests
{
    [Theory]
    [InlineData("resp.SSHKeyID = rd.sshKeyID", "resp.SSHKeyID = rd…")]
    [InlineData("resp.SSHKeyID = rd.sshKeyID", "resp…sshKeyID")]
    [InlineData("t.Errorf(\"route = %d, want %d\", got, want)", "t.Errorf(…)")]
    [InlineData("abcdef", "…def")]
    [InlineData("abcdef", "abc…")]
    [InlineData("abcdef", "a…c…f")]
    [InlineData("logger.Warn(\"x\", a, b)", "logger.Warn(...)")]
    public void AcceptsDeletionsMarkedByAnEllipsis(string original, string replacement) =>
        Assert.True(ReadingElisionRule.IsProjection(original, replacement));

    [Theory]
    [InlineData("abcdef", "abcdef")]
    [InlineData("abcdef", "abcxyz…")]
    [InlineData("abcdef", "…abcdef")]
    [InlineData("abcdef", "fed…")]
    [InlineData("call(a, b)", "call(a, b, c…)")]
    [InlineData("value = compute()", "value = compute() // simplified…")]
    public void RejectsAnythingThatIsNotPureDeletion(string original, string replacement) =>
        Assert.False(ReadingElisionRule.IsProjection(original, replacement));

    // An unmarked deletion is the dangerous case: the line still reads as complete source while
    // silently dropping an argument, a negation, or a guard.
    [Fact]
    public void RejectsADeletionWithNoEllipsis()
    {
        Assert.False(ReadingElisionRule.IsProjection("if (!ok && ready)", "if (ok && ready)"));
        Assert.False(ReadingElisionRule.IsProjection("send(a, b, c)", "send(a, c)"));
    }

    // A trailing ellipsis has to actually elide something, or it is prose appended to the line.
    [Fact]
    public void RejectsATrailingEllipsisThatDeletesNothing() =>
        Assert.False(ReadingElisionRule.IsProjection("abc", "abc…"));

    [Fact]
    public void AppliesToTheSingleOccurrenceOfTheAnchor()
    {
        var applied = ReadingElisionRule.Apply(
            "    resp.SSHKeyID = rd.sshKeyID",
            "rd.sshKeyID",
            "rd…");

        Assert.Equal("    resp.SSHKeyID = rd…", applied);
    }

    // An anchor that appears twice cannot say which one it meant, so it is not applied at all.
    [Fact]
    public void RefusesAnAmbiguousAnchor() =>
        Assert.Null(ReadingElisionRule.Apply("x = f(a) + f(a)", "f(a)", "f(…)"));

    [Fact]
    public void RefusesAnAnchorThatIsNotPresent() =>
        Assert.Null(ReadingElisionRule.Apply("x = 1", "y = 2", "y…"));
}
