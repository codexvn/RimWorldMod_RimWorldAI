using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace RimWorldMCP.Tools
{
    public class Tool_GetMcpLatency : ITool
    {
        public string Name => "get_mcp_latency";
        public string Description => "探查 Agent 与游戏之间的 MCP 延迟：HTTP 往返时间 + 主线程命令队列深度。每次调用返回即时测量值。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { },
            required = System.Array.Empty<string>()
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            var t0 = DateTime.UtcNow.Ticks;

            // 通过 DispatchAsync 测量到主线程的调度延迟
            long dispatchLatencyMs = 0;
            long queueDepthBefore = McpCommandQueue.PendingCount;
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                await McpCommandQueue.DispatchAsync(() =>
                {
                    sw.Stop();
                    dispatchLatencyMs = sw.ElapsedMilliseconds;
                    return true;
                });
            }
            catch (TaskCanceledException) { dispatchLatencyMs = -1; }
            catch (TimeoutException) { dispatchLatencyMs = -2; }

            var queueDepthAfter = McpCommandQueue.PendingCount;
            var totalMs = (DateTime.UtcNow.Ticks - t0) / TimeSpan.TicksPerMillisecond;

            var report = string.Join("\n",
                $"| 指标 | 值 |",
                $"|------|-----|",
                $"| HTTP → Tool 执行总耗时 | {totalMs} ms |",
                $"| 主线程调度延迟 | {(dispatchLatencyMs >= 0 ? $"{dispatchLatencyMs} ms" : dispatchLatencyMs == -1 ? "已取消" : "超时")} |",
                $"| 命令队列积压（调度前） | {queueDepthBefore} |",
                $"| 命令队列积压（调度后） | {queueDepthAfter} |",
                "",
                dispatchLatencyMs switch
                {
                    > 50 => "延迟偏高 — 主线程繁忙或游戏帧率过低。",
                    < 0 => "调度异常 — 主线程可能卡死。",
                    _ => "延迟正常。"
                });

            return ToolResult.Success(report);
        }

        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args) => null;
    }
}
