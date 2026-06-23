using GitBench.Localization;
using Xunit;
using ZGF.Observable;

namespace GitBench.Tests;

public class LocalizationServiceTests
{
    [Fact]
    public void StringsReflectInitialLocale()
    {
        using var service = new LocalizationService(new State<Locale>(Locale.En));
        Assert.Equal("View on GitHub", service.Strings.Value.AboutViewOnGithub);
    }

    [Fact]
    public void ChangingLocalePushesNewCatalogToSubscribers()
    {
        var locale = new State<Locale>(Locale.En);
        using var service = new LocalizationService(locale);

        string? observed = null;
        using var _ = service.Strings.Subscribe(s => observed = s.AboutViewOnGithub);
        Assert.Equal("View on GitHub", observed);

        locale.Value = Locale.Pseudo;

        Assert.NotNull(observed);
        Assert.NotEqual("View on GitHub", observed);
        Assert.Equal(Strings.Pseudo.AboutViewOnGithub, observed);
    }

    [Fact]
    public void PseudoLocaleDiffersFromEnglishForEveryKey()
    {
        Assert.NotEqual(Strings.En.AboutViewOnGithub, Strings.Pseudo.AboutViewOnGithub);
        Assert.NotEqual(Strings.En.AboutCopyright, Strings.Pseudo.AboutCopyright);
    }

    [Fact]
    public void SpanishCatalogIsBakedFromEsJson()
    {
        Assert.Equal("Ver en GitHub", Strings.Es.AboutViewOnGithub);
    }

    [Fact]
    public void SwitchingToSpanishPushesTheSpanishCatalog()
    {
        var locale = new State<Locale>(Locale.En);
        using var service = new LocalizationService(locale);

        string? observed = null;
        using var _ = service.Strings.Subscribe(s => observed = s.AboutViewOnGithub);

        locale.Value = Locale.Es;

        Assert.Equal("Ver en GitHub", observed);
    }

    [Fact]
    public void JapaneseCatalogIsBakedFromJaJson()
    {
        Assert.Equal("GitHubで表示", Strings.Ja.AboutViewOnGithub);
    }

    [Fact]
    public void SwitchingToJapanesePushesTheJapaneseCatalog()
    {
        var locale = new State<Locale>(Locale.En);
        using var service = new LocalizationService(locale);

        string? observed = null;
        using var _ = service.Strings.Subscribe(s => observed = s.AboutViewOnGithub);

        locale.Value = Locale.Ja;

        Assert.Equal("GitHubで表示", observed);
    }

    [Fact]
    public void ChineseAndKoreanCatalogsAreBakedFromTheirJson()
    {
        Assert.Equal("在 GitHub 上查看", Strings.ZhHans.AboutViewOnGithub);
        Assert.Equal("GitHub에서 보기", Strings.Ko.AboutViewOnGithub);
    }

    [Fact]
    public void SwitchingToChineseOrKoreanPushesThatCatalog()
    {
        var locale = new State<Locale>(Locale.En);
        using var service = new LocalizationService(locale);

        string? observed = null;
        using var _ = service.Strings.Subscribe(s => observed = s.AboutViewOnGithub);

        locale.Value = Locale.ZhHans;
        Assert.Equal("在 GitHub 上查看", observed);

        locale.Value = Locale.Ko;
        Assert.Equal("GitHub에서 보기", observed);
    }

    [Fact]
    public void ArabicCatalogIsBakedFromArJson()
    {
        Assert.Equal("العرض على GitHub", Strings.Ar.AboutViewOnGithub);
    }

    [Fact]
    public void SwitchingToArabicPushesThatCatalog()
    {
        var locale = new State<Locale>(Locale.En);
        using var service = new LocalizationService(locale);

        string? observed = null;
        using var _ = service.Strings.Subscribe(s => observed = s.AboutViewOnGithub);

        locale.Value = Locale.Ar;

        Assert.Equal("العرض على GitHub", observed);
    }
}
