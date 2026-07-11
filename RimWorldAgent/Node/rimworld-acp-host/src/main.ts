import { createInterface } from "node:readline";
import { BackendBridge } from "./backend-bridge.js";
import { IpcServer } from "./ipc-server.js";
import { validateEnvelope, MessageTypes, type InitializeRequest, type IpcEnvelope } from "./protocol.js";

async function main(): Promise<void> {
  const input = createInterface({ input: process.stdin, crlfDelay: Infinity });
  const iterator = input[Symbol.asyncIterator]();
  const first = await iterator.next();
  if (first.done) return;

  let bridge: BackendBridge | undefined;
  try {
    const firstMessage = JSON.parse(first.value) as unknown;
    validateEnvelope(firstMessage);
    if (firstMessage.type !== MessageTypes.initialize) {
      throw new Error("The first IPC message must be initialize.");
    }
    const request = firstMessage.payload as InitializeRequest;
    if (!request?.config?.backend?.command) {
      throw new Error("initialize.config.backend.command is required.");
    }

    bridge = new BackendBridge(request.config);
    await bridge.start();
    const server = new IpcServer(bridge);
    await server.run(iterator, firstMessage as IpcEnvelope);
  } catch (error) {
    process.stderr.write(`[host] fatal: ${formatError(error)}\n`);
    if (first.value) {
      const requestId = tryRequestId(first.value);
      process.stdout.write(`${JSON.stringify({
        protocol: "rimworld-agent-ipc",
        version: 1,
        type: "error",
        requestId,
        payload: { code: "host_start_failed", message: formatError(error) },
      })}\n`);
    }
    process.exitCode = 1;
  } finally {
    input.close();
    await bridge?.dispose();
  }
}

function tryRequestId(line: string): string | undefined {
  try {
    const value = JSON.parse(line) as { requestId?: string };
    return value.requestId;
  } catch {
    return undefined;
  }
}

function formatError(error: unknown): string {
  return error instanceof Error ? `${error.name}: ${error.message}` : String(error);
}

main().catch((error) => {
  process.stderr.write(`[host] unhandled: ${formatError(error)}\n`);
  process.exitCode = 1;
});
