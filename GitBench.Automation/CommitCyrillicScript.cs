using System.Diagnostics;
using GitBench.Features.Repos;
using ZGF.Gui;
using ZGF.Gui.Desktop;
using ZGF.Gui.Desktop.Automation;
using ZGF.KeyboardModule;
using ZGF.Observable;

namespace GitBench.Automation;

/// <summary>
/// Drives the real, running GitBench: opens a scratch repo with an uncommitted change, finds the
/// commit box, and types a Cyrillic commit message into it a keystroke at a time. The end-to-end
/// check that non-ASCII text input works in the actual app, not in a test double.
///
/// Runs on its own thread. Every driver call marshals onto the UI thread, so the window keeps
/// painting and the typing is visible as it happens.
/// </summary>
internal static class CommitCyrillicScript
{
    private const string Title = "Исправлен ввод кириллицы";
    private const string Description = "Текст берётся из события ОС, а не из кода клавиши.";
    private const string SecondLine = "Теперь можно писать по-русски. 🚀";

    /// <summary>Starts the script in the background. Call before <c>Run()</c>: the driver needs the UI
    /// thread pumping, which only happens once the app is running.</summary>
    public static void Start(GuiApp app, bool useOsKeyboard)
    {
        var driver = app.CreateDriver();
        driver.UseOsKeyboard = useOsKeyboard;

        var context = app.Context;
        var registry = context.Require<IRepoRegistry>();
        var dispatcher = context.Require<IUiDispatcher>();

        new Thread(() => Run(driver, registry, dispatcher, useOsKeyboard))
        {
            IsBackground = true,
            Name = "GitBench-CommitScript",
        }.Start();
    }

    private static void Run(GuiDriver driver, IRepoRegistry registry, IUiDispatcher dispatcher, bool useOsKeyboard)
    {
        try
        {
            Console.WriteLine($"[script] keystrokes via: {(useOsKeyboard ? "OS SendInput (real WM_CHAR -> GLFW char callback)" : "injected into InputSystem")}");

            var repo = CreateScratchRepo();
            Console.WriteLine($"[script] scratch repo: {repo}");

            // Opening a repo writes observable state the whole UI is bound to, so it belongs on the UI
            // thread like any other model write.
            dispatcher.Post(() => registry.Open(repo));

            // The commit box only exists once the repo has loaded and Local Changes is showing. Wait for
            // the field itself rather than guessing at a delay.
            driver.WaitFor("commit-title", TimeSpan.FromSeconds(30));
            Console.WriteLine("[script] commit box is up");

            if (useOsKeyboard)
            {
                Console.WriteLine("[script] grabbing OS focus — click the GitBench window if Windows refuses it");
                driver.FocusApp();
            }

            driver.Click("commit-title");
            Typist(driver)
                .Pause(0.6f)
                .Type(Title, "typing a Cyrillic commit title")
                .Pause(0.8f)
                .Repeat(KeyboardKey.Backspace, 9, "backspacing the last word")
                .Pause(0.4f)
                .Type("кириллицы", "retyping it")
                .Pause(0.8f)
                .Run();

            driver.Click("commit-description");
            Typist(driver)
                .Type(Description, "typing the description")
                .Press(KeyboardKey.Enter, note: "Enter breaks the line")
                .Type(SecondLine, "a second line, with an emoji")
                .Pause(1.0f)
                .Run();

            var shot = Path.Combine(Path.GetTempPath(), "gitbench-commit-script.png");
            driver.SaveScreenshot(shot);

            // Read the fields back rather than trusting the screenshot: a rendering artifact and a
            // genuinely corrupted buffer look identical in a PNG.
            var typedTitle = driver.TextOf("commit-title");
            var typedDescription = driver.TextOf("commit-description");

            Console.WriteLine($"[script] title:       {typedTitle}");
            Console.WriteLine($"[script] description: {typedDescription.Replace("\n", "\\n")}");
            Console.WriteLine($"[script] screenshot:  {shot}");

            var ok = typedTitle == Title && typedDescription == $"{Description}\n{SecondLine}";
            Console.WriteLine(ok
                ? "[script] PASS — the commit box holds exactly what was typed."
                : "[script] FAIL — the fields don't hold what was typed.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[script] FAIL — {ex.Message}");
        }
    }

    private static Typist Typist(GuiDriver driver)
    {
        var typist = driver.Typist();
        typist.OnNote = note => Console.WriteLine($"[script] {note}");
        return typist;
    }

    /// <summary>A throwaway git repo with one uncommitted file, so Local Changes has something to show
    /// and the commit box is live.</summary>
    private static string CreateScratchRepo()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gitbench-script-{DateTime.Now:HHmmss}");
        Directory.CreateDirectory(path);

        Git(path, "init");
        Git(path, "config user.email script@example.com");
        Git(path, "config user.name Script");

        File.WriteAllText(Path.Combine(path, "README.md"), "# demo\n");
        Git(path, "add .");
        Git(path, "commit -m initial");

        // The uncommitted change that gives the commit box something to commit.
        File.WriteAllText(Path.Combine(path, "README.md"), "# demo\n\nedited by the automation script\n");
        return path;
    }

    private static void Git(string workingDirectory, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("git is not on PATH.");

        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {arguments} failed: {process.StandardError.ReadToEnd().Trim()}");
    }
}
