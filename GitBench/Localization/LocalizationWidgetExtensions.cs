using ZGF.Gui;

namespace GitBench.Localization;

public static class LocalizationWidgetExtensions
{
    public static ILocalizationService Localization(this Context ctx) =>
        ctx.Require<ILocalizationService>();
}
