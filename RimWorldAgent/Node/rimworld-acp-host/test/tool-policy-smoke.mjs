import assert from "node:assert/strict";
import { BackendBridge } from "../dist/backend-bridge.js";

function createConfig() {
  return {
    backend: {
      name: "any-backend",
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

const bridge = new BackendBridge(createConfig());
assert.deepEqual(bridge.toAcpMcpServers(), [
  { type: "http", name: "agent", url: "http://localhost:9878/mcp", headers: [] },
]);

// without permissionAsk, non-MCP falls back to reject/cancel path
const rejected = await bridge.requestPermission({
  toolCall: { title: "Bash" },
  options: [
    { kind: "allow_once", optionId: "allow" },
    { kind: "reject_once", optionId: "reject" },
  ],
});
assert.equal(rejected.outcome.optionId, "reject");

// with permissionAsk, Node forwards to C# decision
let seen = null;
bridge.setPermissionAsk(async (params) => {
  seen = params;
  return { outcome: { outcome: "selected", optionId: "always" } };
});
const allowed = await bridge.requestPermission({
  toolCall: { title: "mcp.agent.get_skills" },
  options: [
    { kind: "allow_always", optionId: "always" },
    { kind: "reject_once", optionId: "reject" },
  ],
});
assert.equal(allowed.outcome.optionId, "always");
assert.equal(seen.toolCall.title, "mcp.agent.get_skills");

console.log("ACP tool policy smoke test passed");
