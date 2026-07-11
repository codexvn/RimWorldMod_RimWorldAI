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

const sessionResponse = createEnvelope(MessageTypes.newSessionResponse, "smoke-new", {
  sessionId: "sess-1",
  configOptions: [
    {
      id: "model",
      name: "Model",
      category: "model",
      type: "select",
      currentValue: "model-1",
      options: [{ value: "model-1", name: "Model 1" }],
    },
    {
      id: "fast",
      name: "Fast",
      type: "boolean",
      currentValue: false,
    },
  ],
});
validateEnvelope(sessionResponse);

const setConfig = createEnvelope(MessageTypes.setSessionConfigOption, "smoke-set", {
  sessionId: "sess-1",
  configId: "model",
  value: "model-1",
});
validateEnvelope(setConfig);

const setConfigResponse = createEnvelope(MessageTypes.setSessionConfigOptionResponse, "smoke-set", {
  sessionId: "sess-1",
  configOptions: sessionResponse.payload.configOptions,
});
validateEnvelope(setConfigResponse);

console.log("IPC protocol smoke test passed");
