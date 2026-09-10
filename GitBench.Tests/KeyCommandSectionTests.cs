using GitBench.Input;
using Xunit;

namespace GitBench.Tests;

public class KeyCommandSectionTests
{
    [Fact]
    public void EveryCommandBelongsToASection()
    {
        foreach (var command in Enum.GetValues<KeyCommand>())
            Assert.Contains(KeyCommandSections.Of(command), KeyCommandSections.All);
    }

    [Fact]
    public void TheSectionsListEveryCommandExactlyOnce()
    {
        var listed = new List<KeyCommand>();
        foreach (var section in KeyCommandSections.All)
            listed.AddRange(KeyCommandSections.CommandsIn(section));

        Assert.Equal(Enum.GetValues<KeyCommand>().Order(), listed.Order());
        Assert.Equal(listed.Count, listed.Distinct().Count());
    }

    [Fact]
    public void NoSectionIsEmpty()
    {
        foreach (var section in KeyCommandSections.All)
            Assert.NotEmpty(KeyCommandSections.CommandsIn(section));
    }
}
