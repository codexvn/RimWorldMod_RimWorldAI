import { randomUUID } from "node:crypto";
import { BackendBridge } from "./backend-bridge.js";
import {
  assertRequestId,
  createEnvelope,
  createError,
  MessageTypes,
  type IpcEnvelope,
  type InitializeRequest,
  type SessionRequest,
  type PromptRequest,
  type InitializeResponse,
  type PromptResponse,
  type SessionResponse,
  type SetSessionConfigOptionRequest,
  type SetSessionConfigOptionResponse,
  type CancelResponse,
  type CloseResponse,
  type PermissionRequest,
  type PermissionResponse,
  validateEnvelope,
} from "./protocol.js";

const MAX_IPC_LINE_BYTES = 4 * 1024 * 1024;

export class IpcServer {
  private readonly bridge: BackendBridge;
  private readonly inFlight = new Set<Promise<void>>();
  private writeQueue = Promise.resolve();
  private readonly permissionWaiters = new Map<string, {
    resolve: (value: PermissionResponse) => void;
    reject: (error: Error) => void;
    timer: ReturnType<typeof setTimeout>;
  }>();

  constructor(bridge: BackendBridge) {
    this.bridge = bridge;
    this.bridge.setEventSink((event) => this.write(createEnvelope(MessageTypes.event, undefined, event)));
    this.bridge.setPermissionAsk((params) => this.askPermission(params));
  }

  private askPermission(params: any): Promise<PermissionResponse> {
    const requestId = randomUUID().replace(/-/g, "");
    return new Promise<PermissionResponse>((resolve, reject) => {
      const timer = setTimeout(() => {
        const waiter = this.permissionWaiters.get(requestId);
        if (!waiter) return;
        this.permissionWaiters.delete(requestId);
        waiter.reject(new Error("permission request timed out"));
      }, 120_000);
      this.permissionWaiters.set(requestId, { resolve, reject, timer });
      const payload: PermissionRequest = { params };
      this.write(createEnvelope(MessageTypes.permissionRequest, requestId, payload));
    });
  }

  private settlePermissionWaiter(requestId: string, response?: PermissionResponse, error?: Error): void {
    const waiter = this.permissionWaiters.get(requestId);
    if (!waiter) return;
    this.permissionWaiters.delete(requestId);
    clearTimeout(waiter.timer);
    if (error) waiter.reject(error);
    else if (response) waiter.resolve(response);
  }

  private rejectAllPermissionWaiters(reason: string): void {
    for (const [requestId, waiter] of this.permissionWaiters) {
      clearTimeout(waiter.timer);
      waiter.reject(new Error(reason));
      this.permissionWaiters.delete(requestId);
    }
  }

  async run(lines: AsyncIterator<string>, first: IpcEnvelope): Promise<void> {
    try {
      await this.dispatch(first);
      for (;;) {
        const next = await lines.next();
        if (next.done) break;
        const line = next.value;
        if (!line) continue;
        if (Buffer.byteLength(line, "utf8") > MAX_IPC_LINE_BYTES) {
          this.write(createError(undefined, "message_too_large", "IPC message exceeds the 4 MiB limit."));
          continue;
        }
        try {
          const message = JSON.parse(line.trim()) as unknown;
          validateEnvelope(message);
          const task = this.dispatch(message);
          this.inFlight.add(task);
          void task.finally(() => this.inFlight.delete(task));
        } catch (error) {
          this.write(createError(undefined, "invalid_message", formatError(error)));
        }
      }
      await Promise.allSettled(Array.from(this.inFlight));
    } finally {
      this.rejectAllPermissionWaiters("IPC server stopped before permission response");
    }
  }

  private async dispatch(message: IpcEnvelope): Promise<void> {
    try {
      if (message.protocol !== "rimworld-agent-ipc" || message.version !== 1) {
        throw new Error("Unsupported IPC protocol or version.");
      }
      const requestId = assertRequestId(message);
      switch (message.type) {
        case MessageTypes.initialize: {
          const request = message.payload as InitializeRequest;
          const response: InitializeResponse = await this.bridge.initialize();
          this.write(createEnvelope(MessageTypes.initializeResponse, requestId, response));
          return;
        }
        case MessageTypes.newSession: {
          const response: SessionResponse = await this.bridge.newSession();
          this.write(createEnvelope(MessageTypes.newSessionResponse, requestId, response));
          return;
        }
        case MessageTypes.resumeSession: {
          const request = message.payload as SessionRequest;
          const response: SessionResponse = await this.bridge.resumeSession(request.sessionId);
          this.write(createEnvelope(MessageTypes.resumeSessionResponse, requestId, response));
          return;
        }
        case MessageTypes.loadSession: {
          const request = message.payload as SessionRequest;
          const response: SessionResponse = await this.bridge.loadSession(request.sessionId);
          this.write(createEnvelope(MessageTypes.loadSessionResponse, requestId, response));
          return;
        }
        case MessageTypes.setSessionConfigOption: {
          const request = message.payload as SetSessionConfigOptionRequest;
          const response: SetSessionConfigOptionResponse = await this.bridge.setSessionConfigOption(request);
          this.write(createEnvelope(MessageTypes.setSessionConfigOptionResponse, requestId, response));
          return;
        }
        case MessageTypes.prompt: {
          const request = message.payload as PromptRequest;
          const response: PromptResponse = await this.bridge.prompt(request.sessionId, request.prompt);
          this.write(createEnvelope(MessageTypes.promptResponse, requestId, response));
          return;
        }
        case MessageTypes.cancel: {
          const request = message.payload as SessionRequest;
          const response: CancelResponse = await this.bridge.cancel(request.sessionId);
          this.write(createEnvelope(MessageTypes.cancelResponse, requestId, response));
          return;
        }
        case MessageTypes.close: {
          const request = message.payload as SessionRequest;
          const response: CloseResponse = await this.bridge.close(request.sessionId);
          this.write(createEnvelope(MessageTypes.closeResponse, requestId, response));
          return;
        }
        case MessageTypes.permissionResponse: {
          const response = message.payload as PermissionResponse;
          this.settlePermissionWaiter(requestId, response);
          return;
        }
        default:
          throw new Error(`Unsupported IPC message type: ${message.type}`);
      }
    } catch (error) {
      this.write(createError(message.requestId, "request_failed", `${message.type}: ${formatError(error)}`));
    }
  }

  private write(message: IpcEnvelope): void {
    const line = JSON.stringify(message);
    if (Buffer.byteLength(line, "utf8") > MAX_IPC_LINE_BYTES) {
      if (message.type !== MessageTypes.error) {
        this.write(createError(message.requestId, "message_too_large", "IPC response exceeds the 4 MiB limit."));
      } else {
        process.stderr.write("[host] IPC error response exceeds the 4 MiB limit.\n");
      }
      return;
    }
    this.writeQueue = this.writeQueue
      .then(async () => {
        process.stdout.write(`${line}\n`);
      })
      .catch((error) => {
        process.stderr.write(`[host] stdout write failed: ${formatError(error)}\n`);
      });
  }
}

function formatError(error: unknown): string {
  return error instanceof Error ? `${error.name}: ${error.message}` : String(error);
}
