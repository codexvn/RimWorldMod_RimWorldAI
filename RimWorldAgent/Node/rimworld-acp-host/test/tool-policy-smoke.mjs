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

const claudeBridge = new BackendBridge(createConfig("claude-agent-acp"));
const meta = claudeBridge.createSessionMeta();
assert.equal(meta.disableBuiltInTools, true);
assert.deepEqual(meta.claudeCode, { options: { tools: [] } });
assert.deepEqual(claudeBridge.toAcpMcpServers(), [
  { type: "http", name: "agent", url: "http://localhost:9878/mcp", headers: [] },
]);

const allowed = await claudeBridge.requestPermission({
  options: [
    { kind: "allow_always", optionId: "always" },
    { kind: "reject_once", optionId: "reject" },
  ],
});
assert.equal(allowed.outcome.optionId, "always");

const customBridge = new BackendBridge(createConfig("custom-backend"));
const rejected = await customBridge.requestPermission({
  options: [
    { kind: "allow_once", optionId: "allow" },
    { kind: "reject_once", optionId: "reject" },
  ],
});
assert.equal(rejected.outcome.optionId, "reject");

console.log("ACP tool policy smoke test passed");
