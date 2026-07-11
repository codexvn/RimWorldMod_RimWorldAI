const enabled = process.env.RIMWORLD_AGENT_IPC_TRACE === "1";

export function trace(message: string): void {
  if (enabled) process.stderr.write(`[host][trace] ${message}\n`);
}
