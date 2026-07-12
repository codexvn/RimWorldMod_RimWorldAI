import * as AjvModule from "ajv/dist/2020.js";
import { existsSync, readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

export const IPC_PROTOCOL = "rimworld-agent-ipc" as const;
export const IPC_VERSION = 1 as const;

export const MessageTypes = {
  initialize: "initialize",
  initializeResponse: "initialize_response",
  newSession: "new_session",
  newSessionResponse: "new_session_response",
  resumeSession: "resume_session",
  resumeSessionResponse: "resume_session_response",
  loadSession: "load_session",
  loadSessionResponse: "load_session_response",
  setSessionConfigOption: "set_session_config_option",
  setSessionConfigOptionResponse: "set_session_config_option_response",
  prompt: "prompt",
  promptResponse: "prompt_response",
  cancel: "cancel",
  cancelResponse: "cancel_response",
  close: "close",
  closeResponse: "close_response",
  event: "event",
  error: "error",
  permissionRequest: "permission_request",
  permissionResponse: "permission_response",
} as const;

export type MessageType = (typeof MessageTypes)[keyof typeof MessageTypes];

export interface IpcEnvelope<T = unknown> {
  protocol: typeof IPC_PROTOCOL;
  version: typeof IPC_VERSION;
  type: string;
  requestId?: string;
  payload?: T;
  meta?: Record<string, unknown>;
}

export interface InitializeRequest {
  hostVersion: string;
  config: AgentRuntimeConfig;
}

export interface InitializeResponse {
  protocolVersion: number;
  agentName: string;
  agentVersion?: string;
  loadSession: boolean;
  resumeSession: boolean;
}

export interface SessionRequest {
  sessionId: string;
}

export interface SessionConfigOption {
  id: string;
  name: string;
  description?: string | null;
  category?: string | null;
  type: string;
  currentValue?: unknown;
  options?: unknown[];
  [key: string]: unknown;
}

export interface SessionResponse {
  sessionId: string;
  configOptions?: SessionConfigOption[];
}

export interface SetSessionConfigOptionRequest {
  sessionId: string;
  configId: string;
  type?: string;
  value: string | boolean;
}

export interface SetSessionConfigOptionResponse {
  sessionId: string;
  configOptions: SessionConfigOption[];
}

export interface PromptRequest {
  sessionId: string;
  prompt: string;
}

export interface PromptResponse {
  sessionId: string;
  stopReason: string;
  status: "success" | "error";
  inputTokens?: number;
  outputTokens?: number;
  cacheReadTokens?: number;
  cacheCreateTokens?: number;
}

export interface CancelResponse {
  sessionId: string;
  cancelled: boolean;
}

export interface CloseResponse {
  closed: boolean;
}

export interface PermissionRequest {
  params: unknown;
}

export interface PermissionResponse {
  outcome: {
    outcome: string;
    optionId?: string;
    [key: string]: unknown;
  };
}

export interface ErrorResponse {
  code: string;
  message: string;
}

export interface AgentEvent {
  kind: string;
  sessionId?: string;
  messageId?: string;
  text?: string;
  toolCallId?: string;
  title?: string;
  toolKind?: string;
  status?: string;
  rawInput?: unknown;
  rawOutput?: unknown;
  isError?: boolean;
  inputTokens?: number;
  outputTokens?: number;
  cacheReadTokens?: number;
  cacheCreateTokens?: number;
  contextWindow?: number;
  usedTokens?: number;
  sizeTokens?: number;
  titleText?: string;
}

export interface AgentRuntimeConfig {
  backend: BackendLaunch;
  cwd: string;
  additionalDirectories: string[];
  prompt: PromptConfig;
  agentMcpUrl: string;
}

export interface BackendLaunch {
  name: string;
  command: string;
  args: string[];
  workingDirectory: string;
  environment: Record<string, string>;
}

export interface PromptConfig {
  systemPrompt: string;
}

const moduleDir = dirname(fileURLToPath(import.meta.url));
// 协议 schema 只维护一份源：RimWorldAgent/IPC/schema/ipc.schema.json
// - 开发/仓库内运行：回退读取 IPC 源
// - 发布产物：构建会把源复制到 rimworld-acp-host/schema/
const schemaCandidates = [
  resolve(moduleDir, "../schema/ipc.schema.json"),                 // 发布/构建输出旁
  resolve(moduleDir, "../../../IPC/schema/ipc.schema.json"),       // 源码树权威源
];
const schemaPath = schemaCandidates.find((candidate) => existsSync(candidate));
if (!schemaPath) {
  throw new Error(
    "IPC schema file not found. Expected build output at rimworld-acp-host/schema/ipc.schema.json or source at RimWorldAgent/IPC/schema/ipc.schema.json.",
  );
}
const schema = JSON.parse(readFileSync(schemaPath, "utf8").replace(/^\uFEFF/, "")) as object;
const AjvConstructor = ((AjvModule as unknown as { default?: unknown }).default ?? AjvModule) as new (options?: object) => { compile(schema: object): ((value: unknown) => boolean) & { errors?: Array<{ instancePath: string; message?: string }> } };
const validator = new AjvConstructor({ allErrors: true, strict: false }).compile(schema);

export function validateEnvelope(value: unknown): asserts value is IpcEnvelope {
  if (!validator(value)) {
    const details = (validator.errors ?? []).map((error) => `${error.instancePath} ${error.message}`).join("; ");
    throw new Error(`Invalid IPC envelope: ${details}`);
  }
}

export function createEnvelope<T>(type: string, requestId: string | undefined, payload: T): IpcEnvelope<T> {
  return { protocol: IPC_PROTOCOL, version: IPC_VERSION, type, ...(requestId ? { requestId } : {}), payload };
}

export function assertRequestId(message: IpcEnvelope): string {
  if (!message.requestId) throw new Error(`IPC request '${message.type}' is missing requestId`);
  return message.requestId;
}

export function createError(requestId: string | undefined, code: string, message: string): IpcEnvelope<ErrorResponse> {
  return createEnvelope(MessageTypes.error, requestId, { code, message });
}
