using ZGF.Observable;

namespace GitBench.Localization;

internal sealed class LocalizationService : ILocalizationService, IDisposable
{
    private readonly Derived<Strings> _strings;

    public IReadable<Strings> Strings => _strings;

    public LocalizationService(State<Locale> locale)
    {
        _strings = new Derived<Strings>(() => global::GitBench.Localization.Strings.For(locale.Value));
    }

    public void Dispose() => _strings.Dispose();
}
