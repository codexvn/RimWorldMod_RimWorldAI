using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using RimWorld;
using Verse;

namespace RimWorldMCP.Tools
{
    public class Tool_GetNotifications : ITool, INoMapRequired
    {
        public string Name => "get_notifications";
        public string Description => "列出当前游戏内所有未关闭的信封通知（Letter），以及活跃警报。用于查看需要处理的游戏事件。";

        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args) => null;

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { }
        });

        public Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            var sb = new StringBuilder();
            var letters = Find.LetterStack.LettersListForReading;
            if (letters.Count == 0)
                sb.AppendLine("当前没有信封通知。");
            else
            {
                sb.AppendLine($"## 信封通知 ({letters.Count})");
                sb.AppendLine();
                for (int i = 0; i < letters.Count; i++)
                {
                    var let = letters[i];
                    var typeName = let.def.label ?? let.def.defName;
                    var label = let.Label;
                    var canDismiss = let.CanDismissWithRightClick;
                    sb.AppendLine($"[{i}] ID={let.ID} | {typeName} | {label}{(canDismiss ? "" : " (不可关闭)")}");
                    if (let is ChoiceLetter cl && !cl.Text.NullOrEmpty() && cl.Text != label)
                    {
                        var text = cl.Text;
                        sb.AppendLine($"    {text}");
                    }
                }
            }

            var alerts = Harmony.NotificationBus.GetActiveAlerts();
            if (alerts.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"## 活跃警报 ({alerts.Count})");
                sb.AppendLine();
                foreach (var alert in alerts)
                {
                    var culprits = alert.Culprits?.Where(c => !string.IsNullOrEmpty(c)).ToArray();
                    var extra = culprits is { Length: > 0 } ? $" ({string.Join("、", culprits.Take(3))})" : "";
                    sb.AppendLine($"- [{alert.Priority}] {alert.Label}{extra}");
                }
            }

            if (letters.Count == 0 && alerts.Count == 0)
                sb.AppendLine("無任何通知或警报。");

            return Task.FromResult(ToolResult.Success(sb.ToString().TrimEnd()));
        }
    }
}
