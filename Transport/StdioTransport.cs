using System;
using System.Threading;
using System.Threading.Tasks;

namespace RimWorldMCP.Transport
{
    public class StdioTransport : ITransport
    {
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        public string Name => "stdio";

        public event Action<string>? OnMessage;

        public Task StartAsync(CancellationToken ct)
        {
            Log("StdioTransport 已启动，等待输入...");
            _ = Task.Run(() => ReadLoop(ct), ct);
            return Task.CompletedTask;
        }

        public async Task SendAsync(string message)
        {
            await _sendLock.WaitAsync();
            try
            {
                await Console.Out.WriteLineAsync(message);
                await Console.Out.FlushAsync();
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public Task StopAsync()
        {
            Log("StdioTransport 已停止");
            return Task.CompletedTask;
        }

        private async Task ReadLoop(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var line = await Console.In.ReadLineAsync();
                    if (line == null) break;
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    Log($"收到: {line.Substring(0, Math.Min(line.Length, 200))}");
                    OnMessage?.Invoke(line);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log($"StdioTransport 错误: {ex.Message}");
            }
        }

        private static void Log(string msg) => McpLog.Info($"[stdio] {msg}");
    }
}
