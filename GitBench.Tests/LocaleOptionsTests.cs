using GitBench.Localization;
using Xunit;

namespace GitBench.Tests;

public class LocaleOptionsTests
{
    [Fact]
    public void EveryLocaleIsOfferedByThePicker()
    {
        foreach (var locale in Enum.GetValues<Locale>())
            Assert.Contains(LocaleOptions.All, o => o.Locale == locale);
    }

    [Fact]
    public void EveryOptionHasADistinctLocaleAndEndonym()
    {
        Assert.Equal(LocaleOptions.All.Count, LocaleOptions.All.Select(o => o.Locale).Distinct().Count());
        Assert.Equal(LocaleOptions.All.Count, LocaleOptions.All.Select(o => o.Endonym).Distinct().Count());
        Assert.All(LocaleOptions.All, o => Assert.False(string.IsNullOrWhiteSpace(o.Endonym)));
    }

    [Fact]
    public void EndonymResolvesEveryDeclaredLocale()
    {
        foreach (var locale in Enum.GetValues<Locale>())
            Assert.False(string.IsNullOrWhiteSpace(LocaleOptions.Endonym(locale)));
    }

    [Fact]
    public void EndonymRejectsALocaleThatIsNotDeclared()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LocaleOptions.Endonym((Locale)999));
    }
}
