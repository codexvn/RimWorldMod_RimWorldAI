using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using Verse.AI;
using RimWorld;

namespace RimWorldMCP.Tools
{
    public class Tool_CommsInsult : ITool
    {
        public string Name => "insult_faction";
        public string Description => "通过通讯台辱骂指定派系（需要 Ideology DLC）。小人走到通讯台后打开派系对话，再用 get_open_dialogs + select_dialog_option 选择辱骂。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                faction_name = new { type = "string", description = "目标派系名称" },
                colonist_id = new { type = "integer", description = "使用的殖民者 ID（可选，不传则自动选最近空闲者）" }
            },
            required = new[] { "faction_name" }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null) return ToolResult.Error("缺少参数");
            if (!args.Value.TryGetProperty("faction_name", out var jFn))
                return ToolResult.Error("缺少必填参数: faction_name");
            string factionName = jFn.GetString() ?? "";
            if (string.IsNullOrWhiteSpace(factionName)) return ToolResult.Error("faction_name 不能为空");

            int? colonistId = null;
            if (args.Value.TryGetProperty("colonist_id", out var jCid) && jCid.TryGetInt32(out var cid))
                colonistId = cid;

            return await CommsHelper.Execute(factionName, colonistId, "骂");
        }
        public (int x, int y)? GetTargetPos(JsonElement? args) => null;
    }
}
