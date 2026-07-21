using System.Text;
using GitBench.App;
using GitBench.Automation;

// Scripted runs of the real GitBench.
//
//   dotnet run --project GitBench.Automation
//   dotnet run --project GitBench.Automation -- --os-input
//
// By default the script's keystrokes are injected straight into the app's InputSystem: focus-
// independent, so the window comes up without stealing focus and you can keep working while it runs.
//
// --os-input instead sends them through the OS (SendInput), so they reach the app via GLFW's real
// callbacks — the only way to exercise the platform layer that injection skips. It needs the window
// to hold OS focus for the whole run, and the driver refuses to type if it ever loses it.
//
// Point GITBENCH_DATA_DIR at a scratch folder to keep a scripted run out of your real repo list.
Console.OutputEncoding = Encoding.UTF8;

var useOsInput = args.Contains("--os-input");

using var host = GitBenchAppHost.Create(startUnfocused: !useOsInput);
CommitCyrillicScript.Start(host.App, useOsInput);
host.Run();
