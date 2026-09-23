// ACP spike: start an agent adapter, run a few prompts, log every message, and reject every
// permission request. See README.md for what each prompt answers.
//
//   dotnet run -- <claude|codex|gemini> <adapters-node_modules-dir> [--allow]
//
// The adapters for claude and codex are the npm packages @agentclientprotocol/claude-agent-acp and
// @agentclientprotocol/codex-acp installed under <adapters-node_modules-dir>; gemini is the global
// @google/gemini-cli started with --experimental-acp.

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: AcpSpike <claude|codex|gemini> <adapters-node_modules-dir>");
    return 2;
}

var adapter = args[0];
var modules = args[1];
var entry = adapter switch
{
    "claude" => Path.Combine(modules, "@agentclientprotocol", "claude-agent-acp", "dist", "index.js"),
    "codex" => Path.Combine(modules, "@agentclientprotocol", "codex-acp", "dist", "index.js"),
    "gemini" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "npm", "node_modules", "@google", "gemini-cli", "dist", "index.js"),
    _ => throw new ArgumentException($"Unknown adapter {adapter}"),
};

var sandbox = Path.Combine(Path.GetTempPath(), "acp-spike-" + adapter);
if (Directory.Exists(sandbox)) Directory.Delete(sandbox, recursive: true);
Directory.CreateDirectory(sandbox);
File.WriteAllText(Path.Combine(sandbox, "notes.txt"), "The secret word is PELICAN.\n");
Run("git", "init -q", sandbox);

var log = new StreamWriter(Path.Combine(AppContext.BaseDirectory, $"acp-{adapter}.log")) { AutoFlush = true };
void Log(string line)
{
    var stamped = $"{DateTime.Now:HH:mm:ss.fff} {line}";
    log.WriteLine(stamped);
    Console.WriteLine(stamped.Length > 400 ? stamped[..400] + "…" : stamped);
}

foreach (var key in new[] { "ANTHROPIC_API_KEY", "OPENAI_API_KEY", "CODEX_API_KEY", "GEMINI_API_KEY", "GOOGLE_API_KEY" })
    Log($"env {key}: {(Environment.GetEnvironmentVariable(key) is null ? "unset" : "SET")}");

var start = new ProcessStartInfo("node")
{
    RedirectStandardInput = true,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    WorkingDirectory = sandbox,
};
start.ArgumentList.Add(entry);
if (adapter == "gemini") start.ArgumentList.Add("--experimental-acp");
foreach (var key in new[] { "ANTHROPIC_API_KEY", "OPENAI_API_KEY", "CODEX_API_KEY", "GEMINI_API_KEY", "GOOGLE_API_KEY" })
    start.Environment.Remove(key);

using var process = Process.Start(start)!;
process.ErrorDataReceived += (_, e) => { if (e.Data is { } d) Log($"stderr {d}"); };
process.BeginErrorReadLine();

var nextId = 0;
var pending = new Dictionary<int, TaskCompletionSource<JsonNode?>>();
var writeLock = new object();
var permissionRequests = 0;
var mcpEcho = false;

void Send(JsonObject message)
{
    message["jsonrpc"] = "2.0";
    var text = message.ToJsonString();
    Log($">> {text}");
    lock (writeLock)
    {
        process.StandardInput.Write(text + "\n");
        process.StandardInput.Flush();
    }
}

Task<JsonNode?> Request(string method, JsonObject parameters)
{
    var id = Interlocked.Increment(ref nextId);
    var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
    lock (pending) pending[id] = tcs;
    Send(new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters });
    return tcs.Task;
}

void Respond(JsonNode id, JsonNode? result) =>
    Send(new JsonObject { ["id"] = id.DeepClone(), ["result"] = result });

void RespondError(JsonNode id, int code, string message) =>
    Send(new JsonObject { ["id"] = id.DeepClone(), ["error"] = new JsonObject { ["code"] = code, ["message"] = message } });

var reader = Task.Run(async () =>
{
    while (await process.StandardOutput.ReadLineAsync() is { } line)
    {
        Log($"<< {line}");
        JsonNode? message;
        try { message = JsonNode.Parse(line); }
        catch (JsonException) { continue; }
        if (message is not JsonObject obj) continue;

        var method = obj["method"]?.GetValue<string>();
        var id = obj["id"];
        if (method is null && id is not null)
        {
            TaskCompletionSource<JsonNode?>? tcs;
            lock (pending) pending.Remove(id.GetValue<int>(), out tcs);
            if (obj["error"] is { } error) tcs?.TrySetException(new Exception(error.ToJsonString()));
            else tcs?.TrySetResult(obj["result"]);
            continue;
        }

        if (method == "session/update")
        {
            var text = obj["params"]?["update"]?.ToJsonString() ?? "";
            if (text.Contains("ECHO:ping")) mcpEcho = true;
            continue;
        }

        if (method == "session/request_permission" && id is not null)
        {
            permissionRequests++;
            var options = obj["params"]!["options"]!.AsArray();
            var toolCall = obj["params"]!["toolCall"];
            Log($"PERMISSION #{permissionRequests}: kind={toolCall?["kind"]} title={toolCall?["title"]}");
            var allow = args.Contains("--allow");
            var wanted = allow ? "allow_once" : "reject_once";
            var chosen = options.FirstOrDefault(o => o?["kind"]?.GetValue<string>() == wanted)
                ?? options.FirstOrDefault(o => o?["kind"]?.GetValue<string>()?.StartsWith(allow ? "allow" : "reject") == true);
            if (chosen is null)
                Respond(id, new JsonObject { ["outcome"] = new JsonObject { ["outcome"] = "cancelled" } });
            else
                Respond(id, new JsonObject
                {
                    ["outcome"] = new JsonObject { ["outcome"] = "selected", ["optionId"] = chosen["optionId"]!.DeepClone() },
                });
            continue;
        }

        if (id is not null)
            RespondError(id, -32601, $"The spike client does not implement {method}.");
    }
});

var init = await Request("initialize", new JsonObject
{
    ["protocolVersion"] = 1,
    ["clientCapabilities"] = new JsonObject
    {
        ["fs"] = new JsonObject { ["readTextFile"] = false, ["writeTextFile"] = false },
        ["terminal"] = false,
    },
    ["clientInfo"] = new JsonObject { ["name"] = "acp-spike", ["version"] = "0.0.1" },
});
Log($"INITIALIZE agentCapabilities={init?["agentCapabilities"]?.ToJsonString()} authMethods={init?["authMethods"]?.ToJsonString()}");

var echoServer = Path.Combine(AppContext.BaseDirectory, "echo-mcp.js");
var node = Which("node");
var session = await Request("session/new", new JsonObject
{
    ["cwd"] = sandbox,
    ["mcpServers"] = new JsonArray
    {
        new JsonObject
        {
            ["name"] = "spike",
            ["command"] = node,
            ["args"] = new JsonArray(echoServer),
            ["env"] = new JsonArray(),
        },
    },
});
var sessionId = session!["sessionId"]!.GetValue<string>();
Log($"SESSION {sessionId} modes={session["modes"]?.ToJsonString()}");

var modeAt = Array.IndexOf(args, "--mode");
if (modeAt >= 0 && modeAt + 1 < args.Length)
{
    var mode = args[modeAt + 1];
    await Request("session/set_mode", new JsonObject { ["sessionId"] = sessionId, ["modeId"] = mode });
    Log($"MODE set to {mode}");
}

var prompts = new[]
{
    ("mcp", "Call the spike_echo tool with text \"ping\" and tell me exactly what it returned. Do nothing else."),
    ("read", "Read notes.txt and tell me the secret word. Do nothing else."),
    ("edit", "Create a file named created.txt in the current directory containing the word hello. Use your file editing tool. If you cannot, say so and stop; do not try any other way."),
    ("shell-read", "Run the shell command `git status` and show me its output. If you cannot, say so and stop."),
    ("shell-write", "Run the shell command `git commit --allow-empty -m spike` in the current directory. If you cannot, say so and stop; do not try any other way."),
};

var results = new List<string>();
foreach (var (name, text) in prompts)
{
    var before = permissionRequests;
    Log($"=== PROMPT {name}");
    var turn = Request("session/prompt", new JsonObject
    {
        ["sessionId"] = sessionId,
        ["prompt"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
    });
    var finished = await Task.WhenAny(turn, Task.Delay(TimeSpan.FromMinutes(3)));
    string stop;
    if (finished != turn)
    {
        Send(new JsonObject { ["method"] = "session/cancel", ["params"] = new JsonObject { ["sessionId"] = sessionId } });
        stop = "TIMEOUT (cancel sent)";
        await Task.WhenAny(turn, Task.Delay(TimeSpan.FromSeconds(20)));
    }
    else
    {
        try { stop = (await turn)?["stopReason"]?.ToJsonString() ?? "?"; }
        catch (Exception e) { stop = "ERROR " + e.Message; }
    }

    var line = $"{name}: stop={stop} permissionRequests={permissionRequests - before}";
    if (name == "mcp") line += $" echoSeen={mcpEcho}";
    if (name == "edit") line += $" created.txt exists={File.Exists(Path.Combine(sandbox, "created.txt"))}";
    if (name == "shell-write") line += $" commit made={Directory.Exists(Path.Combine(sandbox, ".git", "refs", "heads")) && Directory.EnumerateFiles(Path.Combine(sandbox, ".git", "refs", "heads")).Any()}";
    results.Add(line);
    Log($"=== RESULT {line}");
}

Log("=== SUMMARY");
foreach (var line in results) Log(line);

process.StandardInput.Close();
if (!process.WaitForExit(5000)) process.Kill(entireProcessTree: true);
await Task.WhenAny(reader, Task.Delay(2000));
return 0;

static void Run(string file, string arguments, string cwd)
{
    using var p = Process.Start(new ProcessStartInfo(file, arguments) { WorkingDirectory = cwd, UseShellExecute = false })!;
    p.WaitForExit();
}

static string Which(string name)
{
    foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
    {
        foreach (var candidate in new[] { name + ".exe", name })
        {
            var full = Path.Combine(dir, candidate);
            if (File.Exists(full)) return full;
        }
    }

    return name;
}
