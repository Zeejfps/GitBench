// A stdio MCP server with one tool, spike_echo, so the spike can check that an MCP server passed in
// session/new reaches the agent. Newline-delimited JSON-RPC, no dependencies.
const readline = require("readline");
const rl = readline.createInterface({ input: process.stdin });
const send = (m) => process.stdout.write(JSON.stringify(m) + "\n");
rl.on("line", (line) => {
  let msg;
  try { msg = JSON.parse(line); } catch { return; }
  if (msg.id === undefined) return;
  switch (msg.method) {
    case "initialize":
      send({ jsonrpc: "2.0", id: msg.id, result: {
        protocolVersion: msg.params?.protocolVersion ?? "2025-06-18",
        capabilities: { tools: {} },
        serverInfo: { name: "spike", version: "0.0.1" } } });
      break;
    case "tools/list":
      send({ jsonrpc: "2.0", id: msg.id, result: { tools: [{
        name: "spike_echo",
        description: "Echoes the text back, prefixed with ECHO:.",
        inputSchema: { type: "object", properties: { text: { type: "string" } }, required: ["text"] } }] } });
      break;
    case "tools/call":
      process.stderr.write(`spike_echo called: ${JSON.stringify(msg.params)}\n`);
      send({ jsonrpc: "2.0", id: msg.id, result: {
        content: [{ type: "text", text: "ECHO:" + (msg.params?.arguments?.text ?? "") }] } });
      break;
    default:
      send({ jsonrpc: "2.0", id: msg.id, error: { code: -32601, message: "Method not found" } });
  }
});
