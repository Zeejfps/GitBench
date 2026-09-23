# Step 1 spike — Agent Client Protocol adapters

Throwaway. Answers the questions in `docs/plans/pairing-loop.md` § Step 1, and gets deleted once
the app's `AcpAgentConnection` covers the same ground. Not in `GitBench.sln` on purpose.

## Run it

```bash
npm install --prefix <dir> @agentclientprotocol/claude-agent-acp @agentclientprotocol/codex-acp
dotnet build -o <out>
<out>/AcpSpike <claude|codex|gemini> <dir>/node_modules [--mode <modeId>] [--allow]
```

The client advertises no `fs` and no `terminal` capability, passes `echo-mcp.js` as a stdio MCP
server in `session/new`, runs five prompts (call the MCP tool, read a file, create a file, run a
read-only shell command, run a mutating one) and answers every permission request with the
`reject_once` option, or `allow_once` with `--allow`. Every message goes to `acp-<adapter>.log`.

## Findings (2026-09-22, Windows 11)

Versions: claude-agent-acp 0.81.0 over Claude Code 2.1.280; codex-acp 1.13.0 over codex-cli
0.155.0; gemini-cli 0.31.0 with `--experimental-acp`. No API key variable was set for any run.

| | Claude (`claude-agent-acp`) | Codex (`codex-acp`) | Gemini (`--experimental-acp`) |
|---|---|---|---|
| Subscription login, no API key | yes | yes | yes (`oauth-personal`) |
| Default mode | the user's `defaultMode` — `auto` here, **no prompts at all** | `agent`, edits auto-approved in the workspace | `default`, prompts for everything |
| Mode that makes writes ask | `default` ("Manual") via `session/set_mode` | `read-only` ("Ask for approval") | `default` |
| File edit | asked (`kind: edit`), rejected, not written | asked (`kind: edit`), rejected, not written | asked (`kind: edit`), rejected, not written |
| Mutating shell (`git commit`) | asked (`kind: execute`), rejected | fails inside the sandbox, never asks | asked (`kind: execute`), rejected |
| Read-only shell (`git status`) | runs without asking (Claude Code's own read-only classifier) | fails inside the sandbox | asked |
| After a rejection | turn ends `end_turn`, agent says it can't | turn ends **`cancelled`** | turn ends `end_turn` |
| MCP from `session/new` reachable | yes (stdio; `http`+`sse` advertised) | yes (stdio; `http` only) | yes (stdio; `http`+`sse`) |
| MCP tool asks permission | yes, once per tool unless `allow_always` | yes (`_meta.is_mcp_tool_approval`) | yes, offers "Always Allow <server>" |
| How a request names the MCP tool | `toolCall._meta.claudeCode.mcpServer.name` and `toolCall.name` | `rawInput.server` on the earlier `tool_call` update with the same id | only the options' names ("Always Allow <server>") |

**Go for all three**, with these conditions for the client:

1. **Set the mode after `session/new`.** Claude inherits the user's settings, and `auto` or
   `acceptEdits` there means writes never reach the client. The connection picks the adapter's
   asking mode by its id (`default` / `read-only` / `default`) and refuses to start the session if
   the adapter doesn't offer it.
2. **Reject by kind.** `edit`, `delete`, `move` and `execute` are rejected; `read`, `search`,
   `think` and `fetch` are allowed. Claude lets read-only shell through on its own, which is fine:
   the guard is about writes.
3. **Allow the app's own MCP tools.** Every adapter asks before an MCP call. Requests are matched
   to the app's server by the adapter-specific markers above (for Codex, any MCP approval — the
   session is given exactly one server). Choose `allow_once` every time: an `allow_always` from
   Claude Code is written into the repository's own `.claude/settings.local.json`.
4. **A rejection can end the turn** (Codex always, the others when the agent gives up). The session
   re-prompts with "writes are not available; continue with the pairing tools" instead of stalling.
5. **Pass the app's MCP endpoint as `type: "http"`**, which all three accept.
