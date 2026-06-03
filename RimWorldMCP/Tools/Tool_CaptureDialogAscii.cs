using System;
using System.Text.Json;
using System.Threading.Tasks;
using RimWorldMCP.Harmony;

namespace RimWorldMCP.Tools
{
    /// <summary>
    /// 通过 Harmony 补丁自动捕获当前游戏弹框的 IMGUI 控件布局，渲染为 ASCII 文本。
    /// 一次调用完成（HTTP 线程等待下一帧 OnGUI 结束后返回结果）。
    /// </summary>
    public class Tool_CaptureDialogAscii : ITool, INoMapRequired
    {
        public string Name => "capture_dialog_ascii";
        public string Description => "自动捕获当前游戏弹框的控件布局，渲染为 ASCII 文本。支持任意弹框类型（含 Mod 弹框）。一次调用返回结果，无需重试。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                timeout_ms = new
                {
                    type = "integer",
                    description = "捕获超时毫秒数（默认 5000）。如果游戏暂停或 OnGUI 未触发，超过此时间返回错误。"
                }
            }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            int timeoutMs = 5000;
            if (args != null && args.Value.TryGetProperty("timeout_ms", out var jTimeout) && jTimeout.TryGetInt32(out var t))
                timeoutMs = Math.Max(1000, Math.Min(t, 30000)); // 1s ~ 30s

            try
            {
                // ① 主线程：设置捕获标志
                await McpCommandQueue.DispatchAsync(() =>
                {
                    Hook_DialogCapture.BeginCapture();
                    return true;
                });

                // ② HTTP 线程：等待下一帧 OnGUI 完成捕获
                var ascii = await Hook_DialogCapture.WaitForCaptureAsync(timeoutMs);

                if (ascii == null)
                    return ToolResult.Error("弹框捕获超时。可能游戏已暂停或当前无弹框。请确保有打开的弹框且游戏未暂停（无法捕获时需要游戏正常渲染）。");

                if (string.IsNullOrWhiteSpace(ascii))
                    return ToolResult.Success("当前没有检测到弹框控件。（如果游戏中有弹框但结果为空，请重试一次。）");

                return ToolResult.Success(ascii);
            }
            catch (TimeoutException)
            {
                return ToolResult.Error("弹框捕获超时：主线程命令队列无响应。");
            }
            catch (Exception ex)
            {
                return ToolResult.Error($"弹框捕获异常: {ex.GetType().Name}: {ex.Message}");
            }
        }

        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args) => null;
    }
}
