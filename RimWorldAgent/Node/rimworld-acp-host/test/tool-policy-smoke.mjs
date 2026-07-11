import assert from "node:assert/strict";
import { BackendBridge } from "../dist/backend-bridge.js";

function createConfig(backendName) {
  return {
    backend: {
      name: backendName,
      command: "node",
      args: [],
      workingDirectory: ".",
      environment: {},
    },
    cwd: ".",
    additionalDirectories: [],
    prompt: { systemPrompt: "system" },
    agentMcpUrl: "http://localhost:9878/mcp",
  };
}

const bridge = new BackendBridge(createConfig("any-backend"));
const meta = bridge.createSessionMeta();
assert.deepEqual(meta, { systemPrompt: { append: "system" } });
assert.deepEqual(bridge.toAcpMcpServers(), [
  { type: "http", name: "agent", url: "http://localhost:9878/mcp", headers: [] },
]);

const projectedTool = bridge.convertSessionUpdate({
  update: {
    sessionUpdate: "tool_call",
    toolCallId: "tool-1",
    title: "Run command",
    kind: "execute",
    status: "pending",
    rawInput: { command: "Get-Location" },
    _meta: { claudeCode: { toolName: "Bash" } },
  },
});
assert.equal(projectedTool.toolName, "Bash");
assert.equal(projectedTool.title, "Run command");
assert.equal(projectedTool.toolKind, "execute");

const allowed = await bridge.requestPermission({
  toolCall: { title: "mcp__agent__execute_tool" },
  options: [
    { kind: "allow_always", optionId: "always" },
    { kind: "reject_once", optionId: "reject" },
  ],
});
assert.equal(allowed.outcome.optionId, "always");

const rejected = await bridge.requestPermission({
  toolCall: { title: "Bash" },
  options: [
    { kind: "allow_once", optionId: "allow" },
    { kind: "reject_once", optionId: "reject" },
  ],
});
assert.equal(rejected.outcome.optionId, "reject");

console.log("ACP tool policy smoke test passed");
