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

const probeConfig = createConfig();
probeConfig.backend.environment.RIMWORLD_AGENT_MCP_SERVER_NAME = "rimworld_agent_probe";
assert.deepEqual(new BackendBridge(probeConfig).toAcpMcpServers(), [
  { type: "http", name: "rimworld_agent_probe", url: "http://localhost:9878/mcp", headers: [] },
]);

const invalidProbeConfig = createConfig();
invalidProbeConfig.backend.environment.RIMWORLD_AGENT_MCP_SERVER_NAME = "invalid.name";
assert.throws(() => new BackendBridge(invalidProbeConfig).toAcpMcpServers(), /只能包含/);

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

// session meta: empty omit, object pass-through, invalid reject
const emptyMetaBridge = new BackendBridge(createConfig());
const emptyReq = emptyMetaBridge.createSessionRequest();
assert.equal("_meta" in emptyReq, false);

const metaConfig = createConfig();
metaConfig.backend.sessionMetaJson = JSON.stringify({
  disableBuiltInTools: true,
  claudeCode: { options: { settings: { claudeMdExcludes: ["**/CLAUDE.md"] } } },
});
const metaBridge = new BackendBridge(metaConfig);
const metaReq = metaBridge.createSessionRequest();
assert.equal(metaReq._meta.disableBuiltInTools, true);
assert.deepEqual(metaReq._meta.claudeCode.options.settings.claudeMdExcludes, ["**/CLAUDE.md"]);

const badMetaConfig = createConfig();
badMetaConfig.backend.sessionMetaJson = "[]";
const badMetaBridge = new BackendBridge(badMetaConfig);
assert.throws(() => badMetaBridge.createSessionRequest(), /JSON object/);

const invalidJsonConfig = createConfig();
invalidJsonConfig.backend.sessionMetaJson = "{not-json";
const invalidJsonBridge = new BackendBridge(invalidJsonConfig);
assert.throws(() => invalidJsonBridge.createSessionRequest(), /JSON/);

console.log("ACP tool policy smoke test passed");
