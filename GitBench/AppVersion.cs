using System.Reflection;

namespace GitGui;

/// <summary>
/// The running app's version, for display in the UI. Sourced from the assembly's informational
/// version, which CI stamps from the release tag (<c>-p:Version=…</c> in release.yml, matching
/// the <c>vpk pack --packVersion</c>). The SDK's "+gitsha" build-metadata suffix is trimmed off.
/// Plain <c>dotnet run</c> dev builds fall back to the csproj &lt;Version/&gt; default.
/// </summary>
internal static class AppVersion
{
    /// <summary>e.g. "1.2.3" — already prefixed for display as "v{Display}".</summary>
    public static string Display { get; } = Resolve();

    private static string Resolve()
    {
        var info = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            var plus = info.IndexOf('+');
            return plus >= 0 ? info[..plus] : info;
        }

        return Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
    }
}
