using ZGF.Gui;

namespace GitBench.Localization;

public static class L
{
    public static Prop<string?> T(Func<Strings, string> select) =>
        Prop.Deferred<string?>(ctx => ctx.Localization().Strings.Bind(s => (string?)select(s)));
}
