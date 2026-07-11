import assert from "node:assert/strict";
import { MessageTypes, createEnvelope, validateEnvelope } from "../dist/protocol.js";

const valid = createEnvelope(MessageTypes.newSession, "smoke-1", {});
validateEnvelope(valid);

const initialize = createEnvelope(MessageTypes.initialize, "smoke-init", {
  hostVersion: "1.0.0",
  config: {
    backend: { name: "claude-agent-acp", command: "node", args: [], workingDirectory: ".", environment: {} },
    cwd: ".",
    additionalDirectories: [],
    prompt: { systemPrompt: "system" },
    agentMcpUrl: "http://localhost:9878/mcp",
  },
});
validateEnvelope(initialize);

assert.equal(JSON.stringify(initialize).includes('"meta":null'), false);

assert.throws(() => validateEnvelope({
  ...initialize,
  payload: {
    ...initialize.payload,
    config: { ...initialize.payload.config, skills: [] },
  },
}));

validateEnvelope({
  protocol: "rimworld-agent-ipc",
  version: 1,
  type: MessageTypes.error,
  payload: { code: "invalid_message", message: "malformed input" },
});

assert.throws(() => validateEnvelope({
  ...initialize,
  payload: {
    ...initialize.payload,
    config: { ...initialize.payload.config, mcpServers: [] },
  },
}));

assert.throws(() => validateEnvelope({
  protocol: "rimworld-agent-ipc",
  version: 1,
  type: MessageTypes.prompt,
  payload: { sessionId: "only-session" },
}));

console.log("IPC protocol smoke test passed");
