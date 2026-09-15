using System.Diagnostics;
using System.Text;

namespace GitBench.Tests;

internal sealed record GitRun(int ExitCode, string Stdout, string Stderr);

internal static class TestGit
{
    public static string Run(string cwd, params string[] args) => Run(cwd, null, args);

    public static string Run(string cwd, IReadOnlyDictionary<string, string>? env, params string[] args)
    {
        var run = Try(cwd, env, args);
        if (run.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({run.ExitCode}): {run.Stderr}");
        return run.Stdout;
    }

    public static GitRun Try(string cwd, params string[] args) => Try(cwd, null, args);

    public static GitRun Try(string cwd, IReadOnlyDictionary<string, string>? env, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env != null) foreach (var (k, v) in env) psi.Environment[k] = v;

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("git could not be started.");
        proc.StandardInput.Close();
        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return new GitRun(proc.ExitCode, stdout.Result, stderr);
    }

    public static void Init(string path, string branch = "main")
    {
        Directory.CreateDirectory(path);
        Run(path, "init", "-q", "-b", branch);
        Run(path, "config", "user.name", "Test");
        Run(path, "config", "user.email", "test@test");
        Run(path, "config", "commit.gpgsign", "false");
    }
}

internal sealed class TempGitRepo : IDisposable
{
    private readonly TempDir _dir;

    private TempGitRepo(TempDir dir) => _dir = dir;

    public string Path => _dir.Path;

    public static TempGitRepo Init(string branch = "main", string prefix = "gitbench-test-")
    {
        var repo = new TempGitRepo(new TempDir(prefix));
        TestGit.Init(repo.Path, branch);
        return repo;
    }

    public string Git(params string[] args) => TestGit.Run(Path, args);

    public void Write(string relative, string text)
    {
        var full = System.IO.Path.Combine(Path, relative);
        if (System.IO.Path.GetDirectoryName(full) is { } parent) Directory.CreateDirectory(parent);
        File.WriteAllText(full, text);
    }

    public string Commit(string message)
    {
        Git("commit", "-q", "-m", message);
        return Git("rev-parse", "HEAD").Trim();
    }

    public string Commit(string file, string content, string message)
    {
        Write(file, content);
        Git("add", file);
        return Commit(message);
    }

    public void Dispose() => _dir.Dispose();
}
