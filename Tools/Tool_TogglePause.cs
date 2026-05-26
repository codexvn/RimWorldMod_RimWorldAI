using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;

namespace RimWorldMCP.Tools
{
    public class Tool_TogglePause : ITool
    {
        public string Name => "toggle_pause";
        public string Description => "切换游戏暂停状态。如果已暂停则恢复并设为最大速度，如果运行中则暂停。Toggle pause. Resume at max speed or pause.";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new object(),
            required = new string[] { }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    var tm = Find.TickManager;
                    if (tm == null) return ToolResult.Error("TickManager 不可用");

                    var wasPaused = tm.Paused;

                    if (wasPaused)
                    {
                        // 恢复时设置最大速度
                        tm.CurTimeSpeed = TimeSpeed.Superfast;
                    }
                    else
                    {
                        tm.TogglePaused();
                    }

                    var nowPaused = tm.Paused;
                    string state = nowPaused ? "已暂停" : (wasPaused ? "运行中（超快速度）" : "运行中");

                    // 收集无法切换的原因
                    var reasons = new List<string>();

                    // 检查 PlayerCanControl（TogglePaused 第一道门）
                    var pcField = typeof(TickManager).GetProperty("PlayerCanControl",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    if (pcField != null)
                    {
                        var pc = pcField.GetValue(tm);
                        if (pc is AcceptanceReport report && !report)
                        {
                            var reason = report.Reason;
                            if (!string.IsNullOrEmpty(reason))
                                reasons.Add($"无法控制: {reason}");
                            else
                                reasons.Add("无法控制（过场动画或屏幕淡入淡出中）");
                        }
                    }

                    // 检查 ForcePaused（即使切换到 Normal 也保持暂停的窗口）
                    if (tm.ForcePaused && !nowPaused)
                    {
                        reasons.Add("游戏被强制暂停，原因可能是：");
                        var ws = Find.WindowStack;
                        if (ws != null)
                        {
                            for (int i = 0; i < ws.Count; i++)
                            {
                                var w = ws[i];
                                if (w.forcePause)
                                {
                                    var windowName = w.GetType().Name;
                                    reasons.Add($"  - 窗口: {windowName}（已锁定暂停）");
                                }
                            }
                        }
                        if (LongEventHandler.ForcePause)
                            reasons.Add("  - 正在处理长事件");
                        if (Find.TilePicker?.Active == true)
                            reasons.Add("  - 地块选择器激活中");
                    }

                    if (reasons.Count > 0)
                    {
                        var sb = new StringBuilder();
                        sb.AppendLine(wasPaused == nowPaused
                            ? "暂停状态未变化。"
                            : $"游戏当前{state}。");
                        sb.AppendLine();
                        sb.AppendLine("## 注意");
                        foreach (var r in reasons)
                            sb.AppendLine(r);
                        return ToolResult.Success(sb.ToString());
                    }

                    return ToolResult.Success($"游戏当前{state}。");
                }
                catch (System.Exception ex)
                {
                    return ToolResult.Error($"切换暂停失败: {ex.Message}");
                }
            });
        }
        public (int x, int y)? GetTargetPos(JsonElement? args) => null;
    }
}
