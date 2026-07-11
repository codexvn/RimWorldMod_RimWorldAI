import * as acp from "@agentclientprotocol/sdk";
import type { ClientApp, ClientConnection, InitializeResponse as AcpInitializeResponse } from "@agentclientprotocol/sdk";
import { spawn, type ChildProcessWithoutNullStreams } from "node:child_process";
import { existsSync } from "node:fs";
import { delimiter, dirname, isAbsolute, join } from "node:path";
import { Readable, Writable } from "node:stream";
import type {
  AgentEvent,
  AgentRuntimeConfig,
  InitializeResponse,
  PromptResponse,
  SessionResponse,
} from "./protocol.js";
import { trace } from "./trace.js";

export type IpcEventSink = (event: AgentEvent) => void;

export class BackendBridge {
  private readonly config: AgentRuntimeConfig;
  private backendProcess?: ChildProcessWithoutNullStreams;
  private clientConnection?: ClientConnection;
  private eventSink: IpcEventSink = () => undefined;
  private currentSessionId?: string;
  private initialized?: AcpInitializeResponse;
  private disposed = false;

  constructor(config: AgentRuntimeConfig) {
    this.config = config;
  }

  setEventSink(sink: IpcEventSink): void {
    this.eventSink = sink;
  }

  async start(): Promise<void> {
    if (this.backendProcess) return;
    const env = this.createBackendEnvironment();
    const launch = this.resolveBackendLaunch(env);
    trace(`backend spawn name=${this.config.backend.name} shell=${launch.shell}`);
    const child = spawn(launch.command, this.config.backend.args, {
      cwd: this.config.backend.workingDirectory || this.config.cwd,
      env,
      stdio: ["pipe", "pipe", "pipe"],
      windowsHide: true,
      shell: launch.shell,
    });
    this.backendProcess = child;
    child.stderr.on("data", (chunk: Buffer) => process.stderr.write(`[backend] ${chunk.toString()}`));
    child.on("error", (error) => process.stderr.write(`[backend] ${error.name}: ${error.message}\n`));
    child.on("exit", (code, signal) => {
      if (!this.disposed) {
        process.stderr.write(`[backend] exited code=${code ?? "null"} signal=${signal ?? "null"}\n`);
      }
    });

    const input = Writable.toWeb(child.stdin) as unknown as WritableStream<Uint8Array>;
    const output = Readable.toWeb(child.stdout) as unknown as ReadableStream<Uint8Array>;
    const stream = acp.ndJsonStream(input, output);
    const app = this.createClientApp();
    this.clientConnection = app.connect(stream);
  }

  private createBackendEnvironment(): NodeJS.ProcessEnv {
    const env: NodeJS.ProcessEnv = { ...process.env, ...this.config.backend.environment };
    const pathKey = Object.keys(env).find((key) => key.toLowerCase() === "path") ?? "PATH";
    const nodeDirectory = dirname(process.execPath);
    env[pathKey] = [nodeDirectory, env[pathKey]].filter(Boolean).join(delimiter);
    return env;
  }

  private resolveBackendLaunch(env: NodeJS.ProcessEnv): { command: string; shell: boolean } {
    const command = this.config.backend.command.trim();
    if (process.platform !== "win32") return { command, shell: false };

    // Windows cannot spawn .cmd/.bat files directly. Resolve a command such
    // as "npx" to its PATH wrapper so manual backend entries work the same as
    // the built-in templates, without exposing process mode in the UI/DTO.
    if (/\.(cmd|bat)$/i.test(command)) return { command, shell: true };
    const wrapper = this.findWindowsWrapper(command, env);
    return wrapper ? { command: wrapper, shell: true } : { command, shell: false };
  }

  private findWindowsWrapper(command: string, env: NodeJS.ProcessEnv): string | undefined {
    const candidates = command.includes("\\") || command.includes("/") || isAbsolute(command)
      ? [`${command}.cmd`, `${command}.bat`]
      : (env.PATH ?? env.Path ?? "").split(delimiter).flatMap((directory) => [
        join(directory, `${command}.cmd`),
        join(directory, `${command}.bat`),
      ]);
    return candidates.find((candidate) => existsSync(candidate));
  }

  async initialize(): Promise<InitializeResponse> {
    const context = this.requireConnection().agent;
    trace("ACP agent.initialize send");
    this.initialized = await context.request(acp.methods.agent.initialize, {
      protocolVersion: acp.PROTOCOL_VERSION,
      clientCapabilities: {
        fs: {
          readTextFile: false,
          writeTextFile: false,
        },
        terminal: false,
      },
    }) as unknown as AcpInitializeResponse;
    const initialized = this.initialized;
    if (!initialized) throw new Error("ACP initialize returned no response.");
    trace(`ACP agent.initialize receive protocol=${initialized.protocolVersion}`);
    const info = initialized.agentInfo;
    return {
      protocolVersion: initialized.protocolVersion,
      agentName: info?.name ?? this.config.backend.name,
      agentVersion: info?.version,
      loadSession: initialized.agentCapabilities?.loadSession === true,
      resumeSession: initialized.agentCapabilities?.sessionCapabilities?.resume !== undefined && initialized.agentCapabilities.sessionCapabilities.resume !== null,
    };
  }

  async newSession(): Promise<SessionResponse> {
    trace("ACP session/new send");
    const response = await this.requireConnection().agent.request(acp.methods.agent.session.new, this.createSessionRequest()) as { sessionId: string };
    this.currentSessionId = String(response.sessionId);
    trace("ACP session/new receive");
    return { sessionId: this.currentSessionId };
  }

  async resumeSession(sessionId: string): Promise<SessionResponse> {
    trace("ACP session/resume send");
    const response = await this.requireConnection().agent.request(acp.methods.agent.session.resume, {
      sessionId,
      cwd: this.config.cwd,
      additionalDirectories: this.config.additionalDirectories,
      mcpServers: this.toAcpMcpServers(),
      _meta: this.createSessionMeta(),
    }) as unknown as { sessionId?: string };
    this.currentSessionId = String(response.sessionId ?? sessionId);
    trace("ACP session/resume receive");
    return { sessionId: this.currentSessionId };
  }

  async loadSession(sessionId: string): Promise<SessionResponse> {
    trace("ACP session/load send");
    const response = await this.requireConnection().agent.request(acp.methods.agent.session.load, {
      sessionId,
      cwd: this.config.cwd,
      additionalDirectories: this.config.additionalDirectories,
      mcpServers: this.toAcpMcpServers(),
      _meta: this.createSessionMeta(),
    }) as unknown as { sessionId?: string };
    this.currentSessionId = String(response.sessionId ?? sessionId);
    trace("ACP session/load receive");
    return { sessionId: this.currentSessionId };
  }

  async prompt(sessionId: string, prompt: string): Promise<PromptResponse> {
    trace(`ACP session/prompt send textLength=${prompt.length}`);
    const response = await this.requireConnection().agent.request(acp.methods.agent.session.prompt, {
      sessionId,
      prompt: [{ type: "text", text: prompt }],
    }) as unknown as {
      stopReason: string;
      usage?: { inputTokens?: number; outputTokens?: number; cachedReadTokens?: number; cachedWriteTokens?: number };
    };
    trace(`ACP session/prompt receive stopReason=${String(response.stopReason)}`);
    return {
      sessionId,
      stopReason: String(response.stopReason),
      status: "success",
      inputTokens: response.usage?.inputTokens,
      outputTokens: response.usage?.outputTokens,
      cacheReadTokens: response.usage?.cachedReadTokens,
      cacheCreateTokens: response.usage?.cachedWriteTokens,
    };
  }

  async cancel(sessionId: string): Promise<{ sessionId: string; cancelled: boolean }> {
    trace("ACP session/cancel send");
    await this.requireConnection().agent.notify(acp.methods.agent.session.cancel, { sessionId });
    trace("ACP session/cancel complete");
    return { sessionId, cancelled: true };
  }

  async close(sessionId: string): Promise<{ closed: boolean }> {
    trace("ACP session/close send");
    await this.requireConnection().agent.request(acp.methods.agent.session.close, { sessionId });
    trace("ACP session/close receive");
    if (this.currentSessionId === sessionId) this.currentSessionId = undefined;
    return { closed: true };
  }

  async dispose(): Promise<void> {
    this.disposed = true;
    try { await this.clientConnection?.close(); } catch (error) { this.logError("close ACP connection", error); }
    if (this.backendProcess) {
      this.backendProcess.stdin.destroy();
      this.backendProcess.stdout.destroy();
      this.backendProcess.stderr.destroy();
      if (!this.backendProcess.killed) this.backendProcess.kill();
      this.backendProcess.unref();
    }
    this.backendProcess = undefined;
    this.clientConnection = undefined;
  }

  private createClientApp(): ClientApp {
    return acp
      .client({ name: "rimworld-acp-host" })
      .onNotification(acp.methods.client.session.update, ({ params }) => {
        this.eventSink(this.convertSessionUpdate(params));
      })
      .onRequest(acp.methods.client.session.requestPermission, ({ params }) => this.requestPermission(params))
      .onRequest(acp.methods.client.fs.readTextFile, ({ params }) => this.readTextFile(params))
      .onRequest(acp.methods.client.fs.writeTextFile, ({ params }) => this.writeTextFile(params))
      .onRequest(acp.methods.client.terminal.create, ({ params }) => this.createTerminal(params))
      .onRequest(acp.methods.client.terminal.output, () => this.rejectCapability("terminal output"))
      .onRequest(acp.methods.client.terminal.release, () => ({}))
      .onRequest(acp.methods.client.terminal.waitForExit, () => this.rejectCapability("terminal wait"))
      .onRequest(acp.methods.client.terminal.kill, () => ({}));
  }

  private async requestPermission(params: any): Promise<any> {
    if (this.usesControlledClaudeToolSet()) {
      const allowed = (params.options ?? []).find((option: any) => option.kind === "allow_always")
        ?? (params.options ?? []).find((option: any) => option.kind === "allow_once");
      if (allowed) return { outcome: { outcome: "selected", optionId: allowed.optionId } };
    }
    const rejected = (params.options ?? []).find((option: any) => String(option.kind).startsWith("reject"));
    if (rejected) return { outcome: { outcome: "selected", optionId: rejected.optionId } };
    return { outcome: { outcome: "cancelled" } };
  }

  private async readTextFile(_params: any): Promise<any> {
    return this.rejectCapability("file read");
  }

  private async writeTextFile(_params: any): Promise<any> {
    return this.rejectCapability("file write");
  }

  private async createTerminal(_params: any): Promise<any> {
    return this.rejectCapability("terminal");
  }

  private rejectCapability(name: string): never {
    throw new Error(`${name} capability is disabled for the game agent.`);
  }

  private createSessionRequest(): any {
    return {
      cwd: this.config.cwd,
      additionalDirectories: this.config.additionalDirectories,
      mcpServers: this.toAcpMcpServers(),
      _meta: this.createSessionMeta(),
    };
  }

  private createSessionMeta(): Record<string, unknown> {
    const meta: Record<string, unknown> = {
      systemPrompt: { append: this.config.prompt.systemPrompt },
    };
    if (this.usesControlledClaudeToolSet()) {
      meta.disableBuiltInTools = true;
      meta.claudeCode = { options: { tools: [] } };
    }
    return meta;
  }

  private toAcpMcpServers(): any[] {
    return [{ type: "http", name: "agent", url: this.config.agentMcpUrl, headers: [] }];
  }

  private usesControlledClaudeToolSet(): boolean {
    return this.config.backend.name === "claude-agent-acp";
  }

  private convertSessionUpdate(params: any): AgentEvent {
    const update = params.update ?? {};
    trace(`ACP session/update type=${String(update.sessionUpdate ?? "unknown")}`);
    const sessionId = String(params.sessionId ?? this.currentSessionId ?? "");
    switch (update.sessionUpdate) {
      case "agent_message_chunk":
        return { kind: "text_delta", sessionId, messageId: update.messageId, text: update.content?.text ?? "" };
      case "agent_thought_chunk":
        return { kind: "thought_delta", sessionId, messageId: update.messageId, text: update.content?.text ?? "" };
      case "user_message_chunk":
        return { kind: "user_message", sessionId, messageId: update.messageId, text: update.content?.text ?? "" };
      case "tool_call":
        return {
          kind: "tool_call", sessionId, toolCallId: String(update.toolCallId), title: update.title,
          toolKind: String(update.kind), status: String(update.status), rawInput: update.rawInput, rawOutput: update.rawOutput,
        };
      case "tool_call_update":
        return {
          kind: "tool_update", sessionId, toolCallId: String(update.toolCallId), title: update.title,
          toolKind: update.kind ? String(update.kind) : undefined, status: String(update.status),
          rawInput: update.rawInput, rawOutput: update.rawOutput,
        };
      case "usage_update":
        return {
          kind: "usage", sessionId, inputTokens: update.used ?? update.inputTokens,
          cacheReadTokens: update.cachedReadTokens, cacheCreateTokens: update.cachedWriteTokens,
          usedTokens: update.used, sizeTokens: update.size, contextWindow: update.size,
        };
      case "session_info_update":
        return { kind: "session_info", sessionId, titleText: update.title ?? "" };
      case "plan":
        return { kind: "status", sessionId, titleText: "ACP plan updated." };
      default:
        return { kind: "status", sessionId, titleText: `ACP update: ${String(update.sessionUpdate ?? "unknown")}` };
    }
  }

  private requireConnection(): ClientConnection {
    if (!this.clientConnection) throw new Error("ACP backend connection is not ready.");
    return this.clientConnection;
  }

  private logError(operation: string, error: unknown): void {
    const value = error instanceof Error ? `${error.name}: ${error.message}` : String(error);
    process.stderr.write(`[host] ${operation} failed: ${value}\n`);
  }
}
