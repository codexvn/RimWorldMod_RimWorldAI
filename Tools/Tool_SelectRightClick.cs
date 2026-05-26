using System;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;

namespace RimWorldMCP.Tools
{
    public class Tool_SelectRightClick : ITool
    {
        public string Name => "select_right_click";
        public string Description => "选择并执行之前 get_right_click_menu 生成的右键菜单选项。传入选项编号即可执行对应操作。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                option_index = new { type = "integer", description = "选项编号（来自 get_right_click_menu 输出的 [N]）" }
            },
            required = new[] { "option_index" }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null) return ToolResult.Error("缺少参数");
            if (!args.Value.TryGetProperty("option_index", out var jOi) || !jOi.TryGetInt32(out var optIdx))
                return ToolResult.Error("缺少 option_index");

            return await McpCommandQueue.DispatchAsync(() =>
            {
                if (RightClickMenuStore.TrySelect(optIdx, out var result))
                    return ToolResult.Success(result);
                return ToolResult.Error(result);
            });
        }

        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args) => null;
    }
}
