using GitBench.App;
using GitBench.Lsp;

namespace GitBench.Features.LanguageServers;

/// <summary>
/// Whether this run writes down what it says to language servers, and where.
/// </summary>
/// <remarks>
/// <para>
/// Off unless <see cref="EnvVar"/> is <c>1</c> at launch, like the other launch-time modes: a trace
/// records a repository's source text and the paths around it, which is not something a normal run
/// should leave on disk, and the writing is per message on a hot path.
/// </para>
/// <para>
/// The folder is never cleared. A trace is opened because something went wrong once, and the run
/// that reproduces it is rarely the run someone thought to look at; deleting the previous one to
/// keep the folder tidy would throw away the evidence.
/// </para>
/// </remarks>
internal static class LspTracing
{
    public const string EnvVar = "DIFFDINO_LSP_TRACE";

    public const string FolderName = "lsp-traces";

    /// <summary>Read once, at composition time: tracing is a property of the run, not a setting.</summary>
    public static bool IsEnabled => Environment.GetEnvironmentVariable(EnvVar) == "1";

    public static string FolderPath => AppPaths.AppDataPath(FolderName);

    /// <summary>
    /// Where traces go, and a line on the console saying so — a trace nobody can find is the same
    /// as no trace, and the folder is inside per-user application data where nobody would look.
    /// </summary>
    public static ILspTraceSource Source()
    {
        if (!IsEnabled) return NoLspTrace.Instance;

        var folder = FolderPath;
        Console.WriteLine($"{EnvVar}=1: language server traces are being written to {folder}");
        return new LspTraceFolder(folder);
    }
}
