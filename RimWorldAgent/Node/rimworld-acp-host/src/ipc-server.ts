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
  type CancelResponse,
  type CloseResponse,
  validateEnvelope,
} from "./protocol.js";

const MAX_IPC_LINE_BYTES = 4 * 1024 * 1024;

export class IpcServer {
  private readonly bridge: BackendBridge;
  private readonly inFlight = new Set<Promise<void>>();
  private writeQueue = Promise.resolve();

  constructor(bridge: BackendBridge) {
    this.bridge = bridge;
    this.bridge.setEventSink((event) => this.write(createEnvelope(MessageTypes.event, undefined, event)));
  }

  async run(lines: AsyncIterator<string>, first: IpcEnvelope): Promise<void> {
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
