using ZGF.Observable;

namespace GitBench.Localization;

public interface ILocalizationService
{
    IReadable<Strings> Strings { get; }
}
