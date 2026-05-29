using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using RimWorldMCP.Harmony;

namespace RimWorldMCP.Tools
{
    /// <summary>Agent 轮询获取游戏事件（替代旧 SSE 推送）</summary>
    public class Tool_DrainGameEvents : ITool
    {
        public string Name => "drain_game_events";
        public string Description => "获取并清空待处理的游戏事件列表。Agent 应定期轮询此工具以获取新的事件通知。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { },
            required = System.Array.Empty<string>()
        });

        public Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            return McpCommandQueue.DispatchAsync(() =>
            {
                var notifications = NotificationBus.Drain();
                if (notifications.Count == 0)
                    return ToolResult.Success("暂无新事件。");

                var sb = new StringBuilder();
                sb.AppendLine($"## 游戏事件 ({notifications.Count} 件)");
                foreach (var n in notifications)
                {
                    var label = n.Label ?? "";
                    var text = n.Text ?? "";
                    var level = NotificationBus.GetEventLevel(n.Type, n.DangerLabel);
                    var icon = level switch
                    {
                        EventLevel.Critical => "🔴",
                        EventLevel.Warning => "🟡",
                        EventLevel.Info => "ℹ️",
                        _ => ""
                    };
                    var line = $"{icon} [{n.DangerLabel}] {label}";
                    if (!string.IsNullOrEmpty(text) && text != label)
                        line += $" — {text.Substring(0, System.Math.Min(text.Length, 200))}";
                    sb.AppendLine(line);
                }
                return ToolResult.Success(sb.ToString().TrimEnd());
            });
        }

        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args) => null;
    }
}
