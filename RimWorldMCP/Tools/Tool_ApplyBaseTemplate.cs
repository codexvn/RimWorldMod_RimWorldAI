using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace RimWorldMCP.Tools
{
    public class Tool_ApplyBaseTemplate : ITool
    {
        public string Name => "apply_base_template";
        public string Description => "应用基地模板，根据模板名和中心点坐标返回所有房间和墙壁的精确坐标。坐标可直接用于 designate_room。调用前先用 list_base_templates 查看可用模板。坐标范围为闭区间（两端坐标均包含）。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                template_name = new { type = "string", description = "模板名称: single_room, nine_grid, nine_grid_walled, bedroom_row" },
                center_x = new { type = "integer", description = "基地中心 X 坐标" },
                center_y = new { type = "integer", description = "基地中心 Y 坐标" },
                internal_size = new { type = "integer", description = "房间内径（默认 13，13=13x13内径/15x15外径）", @default = 13 },
                options = new { type = "string", description = "模板特定选项，JSON 格式字符串。single_room: {\"door_sides\":\"bottom\"}; bedroom_row: {\"count\":5,\"internal_width\":5,\"internal_height\":5}; nine_grid_walled: {\"wall_thickness\":2}" }
            },
            required = new[] { "template_name", "center_x", "center_y" }
        });

        public Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null) return Task.FromResult(ToolResult.Error("缺少参数"));

            if (!args.Value.TryGetProperty("template_name", out var jName) || jName.GetString() is not string templateName)
                return Task.FromResult(ToolResult.Error("缺少必填参数: template_name"));

            if (!args.Value.TryGetProperty("center_x", out var jCx) || !jCx.TryGetInt32(out var centerX))
                return Task.FromResult(ToolResult.Error("缺少必填参数: center_x"));
            if (!args.Value.TryGetProperty("center_y", out var jCy) || !jCy.TryGetInt32(out var centerY))
                return Task.FromResult(ToolResult.Error("缺少必填参数: center_y"));

            int internalSize = 13;
            if (args.Value.TryGetProperty("internal_size", out var jIs) && jIs.TryGetInt32(out var isVal))
                internalSize = isVal;

            JsonElement? options = null;
            if (args.Value.TryGetProperty("options", out var jOpt) && jOpt.ValueKind == JsonValueKind.String)
            {
                try { options = JsonSerializer.Deserialize<JsonElement>(jOpt.GetString()!); }
                catch { /* ignore invalid JSON, use defaults */ }
            }

            return Task.FromResult(templateName switch
            {
                "single_room" => BuildSingleRoom(centerX, centerY, internalSize, options),
                "nine_grid" => BuildNineGrid(centerX, centerY, internalSize),
                "nine_grid_walled" => BuildNineGridWalled(centerX, centerY, internalSize, options),
                "bedroom_row" => BuildBedroomRow(centerX, centerY, options),
                _ => ToolResult.Error($"未知模板: {templateName}。请用 list_base_templates 查看可用模板。")
            });
        }

        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args)
        {
            if (args == null) return null;
            if (!args.Value.TryGetProperty("center_x", out var jX) || !jX.TryGetInt32(out var cx)) return null;
            if (!args.Value.TryGetProperty("center_y", out var jY) || !jY.TryGetInt32(out var cy)) return null;
            return (cx, cy, cx, cy);
        }

        // ── single_room ──────────────────────────────────────────────
        private static ToolResult BuildSingleRoom(int cx, int cy, int internalSize, JsonElement? options)
        {
            string doorSides = "bottom";
            if (options != null && options.Value.TryGetProperty("door_sides", out var jDs))
                doorSides = jDs.GetString() ?? "bottom";

            int external = internalSize + 2;
            int posX = cx - external / 2;
            int posY = cy - external / 2;
            int endX = posX + external - 1;
            int endY = posY + external - 1;
            int internalArea = internalSize * internalSize;

            var sb = new StringBuilder();
            sb.AppendLine($"## 模板: single_room (中心: {cx}, {cy})");
            sb.AppendLine();
            sb.AppendLine("### 房间");
            sb.AppendLine($"- 范围: pos=({posX},{posY}) end=({endX},{endY})");
            sb.AppendLine($"- 内径: {internalSize}×{internalSize} = {internalArea} 格");
            sb.AppendLine($"- 外径: {external}×{external}（含墙体）");
            sb.AppendLine($"- 建议门: {doorSides}");
            sb.AppendLine();
            sb.AppendLine("### 建造");
            sb.AppendLine($"designate_room(pos_x={posX}, pos_y={posY}, end_x={endX}, end_y={endY}, door_positions={doorSides})");

            return ToolResult.Success(sb.ToString().TrimEnd());
        }

        // ── nine_grid ─────────────────────────────────────────────────
        private static ToolResult BuildNineGrid(int cx, int cy, int internalSize)
        {
            int stride = internalSize + 1;       // +1 = shared wall
            int total = 3 * stride + 1;          // +1 = first room's left/top wall
            int originX = cx - total / 2;
            int originY = cy - total / 2;

            var rooms = new List<(int row, int col, int px, int py, int ex, int ey, string doors, string label)>();
            string[,] labels = { { "左上", "上中", "右上" }, { "左中", "中心", "右中" }, { "左下", "下中", "右下" } };

            for (int row = 0; row < 3; row++)
            {
                for (int col = 0; col < 3; col++)
                {
                    int px = originX + col * stride;
                    int py = originY + row * stride;
                    int ex = px + internalSize + 1;
                    int ey = py + internalSize + 1;

                    // Door suggestions: outer-facing sides + center room connects to all
                    var doorList = new List<string>();
                    bool isTop = row == 0, isBottom = row == 2;
                    bool isLeft = col == 0, isRight = col == 2;
                    bool isCenter = row == 1 && col == 1;

                    if (isCenter)
                    {
                        doorList.AddRange(new[] { "top", "bottom", "left", "right" });
                    }
                    else
                    {
                        if (isTop) doorList.Add("top");
                        if (isBottom) doorList.Add("bottom");
                        if (isLeft) doorList.Add("left");
                        if (isRight) doorList.Add("right");
                    }

                    rooms.Add((row, col, px, py, ex, ey, string.Join(",", doorList), labels[row, col]));
                }
            }

            var sb = new StringBuilder();
            sb.AppendLine($"## 模板: nine_grid (中心: {cx}, {cy})");
            sb.AppendLine();
            sb.AppendLine($"网格: 3×3 房间，内径 {internalSize}×{internalSize}，共用墙，总占地 {total}×{total}");
            sb.AppendLine();

            sb.AppendLine("### 房间 (9间)");
            foreach (var (row, col, px, py, ex, ey, doors, label) in rooms)
            {
                int area = internalSize * internalSize;
                sb.AppendLine($"[{row},{col}] {label} — pos=({px},{py}) end=({ex},{ey}) 内部{area}格 建议门: {doors}");
            }

            sb.AppendLine();
            sb.AppendLine("### 建造顺序");
            sb.AppendLine("1. designate_room 建造所有9个房间（任意顺序，共用墙自动跳过）");

            sb.AppendLine();
            sb.AppendLine("### ASCII 布局");
            sb.AppendLine("   ┌────────┬────────┬────────┐");
            for (int row = 0; row < 3; row++)
            {
                sb.AppendLine("   │        │        │        │");
                var labels_row = new string[3];
                for (int col = 0; col < 3; col++)
                    labels_row[col] = $" [{row},{col}]";
                sb.AppendLine($"   │{labels_row[0],-8}│{labels_row[1],-8}│{labels_row[2],-8}│");
                sb.AppendLine("   │        │        │        │");
                if (row < 2)
                    sb.AppendLine("   ├────────┼────────┼────────┤");
                else
                    sb.AppendLine("   └────────┴────────┴────────┘");
            }

            return ToolResult.Success(sb.ToString().TrimEnd());
        }

        // ── nine_grid_walled ──────────────────────────────────────────
        private static ToolResult BuildNineGridWalled(int cx, int cy, int internalSize, JsonElement? options)
        {
            int wallThickness = 2;
            if (options != null && options.Value.TryGetProperty("wall_thickness", out var jWt) && jWt.TryGetInt32(out var wt))
                wallThickness = wt;

            int buffer = 2; // gap between rooms and outer wall
            int stride = internalSize + 1;
            int innerTotal = 3 * stride + 1;
            int originX = cx - innerTotal / 2;
            int originY = cy - innerTotal / 2;

            // Inner rooms (same as nine_grid)
            var rooms = new List<(int row, int col, int px, int py, int ex, int ey, string doors, string label)>();
            string[,] labels = { { "左上", "上中", "右上" }, { "左中", "中心", "右中" }, { "左下", "下中", "右下" } };

            for (int row = 0; row < 3; row++)
            {
                for (int col = 0; col < 3; col++)
                {
                    int px = originX + col * stride;
                    int py = originY + row * stride;
                    int ex = px + internalSize + 1;
                    int ey = py + internalSize + 1;

                    var doorList = new List<string>();
                    bool isTop = row == 0, isBottom = row == 2;
                    bool isLeft = col == 0, isRight = col == 2;
                    bool isCenter = row == 1 && col == 1;

                    if (isCenter) doorList.AddRange(new[] { "top", "bottom", "left", "right" });
                    else
                    {
                        if (isTop) doorList.Add("top");
                        if (isBottom) doorList.Add("bottom");
                        if (isLeft) doorList.Add("left");
                        if (isRight) doorList.Add("right");
                    }

                    rooms.Add((row, col, px, py, ex, ey, string.Join(",", doorList), labels[row, col]));
                }
            }

            // Outer wall segments
            int wallStartX = originX - buffer - wallThickness;
            int wallStartY = originY - buffer - wallThickness;
            int wallEndX = originX + innerTotal + buffer - 1;
            int wallEndY = originY + innerTotal + buffer - 1;
            int outerTotalW = innerTotal + 2 * buffer + 2 * wallThickness;
            int outerTotalH = innerTotal + 2 * buffer + 2 * wallThickness;

            var sb = new StringBuilder();
            sb.AppendLine($"## 模板: nine_grid_walled (中心: {cx}, {cy})");
            sb.AppendLine();
            sb.AppendLine($"内层: 3×3 房间，内径 {internalSize}×{internalSize}，共用墙");
            sb.AppendLine($"围墙: {wallThickness} 格厚，缓冲带 {buffer} 格");
            sb.AppendLine($"总占地: {outerTotalW}×{outerTotalH}");
            sb.AppendLine();

            sb.AppendLine("### 内层房间 (9间)");
            foreach (var (row, col, px, py, ex, ey, doors, label) in rooms)
            {
                int area = internalSize * internalSize;
                sb.AppendLine($"[{row},{col}] {label} — pos=({px},{py}) end=({ex},{ey}) 内部{area}格 建议门: {doors}");
            }

            sb.AppendLine();
            sb.AppendLine("### 外围防御墙 (4段)");
            sb.AppendLine("用 designate_room 建造（不设门和地板，纯墙体）:");
            sb.AppendLine(
                $"- 北墙: pos=({wallStartX},{wallStartY}) end=({wallEndX},{wallStartY + wallThickness - 1})");
            sb.AppendLine(
                $"- 南墙: pos=({wallStartX},{wallEndY - wallThickness + 1}) end=({wallEndX},{wallEndY})");
            sb.AppendLine(
                $"- 西墙: pos=({wallStartX},{wallStartY + wallThickness}) end=({wallStartX + wallThickness - 1},{wallEndY - wallThickness})");
            sb.AppendLine(
                $"- 东墙: pos=({wallEndX - wallThickness + 1},{wallStartY + wallThickness}) end=({wallEndX},{wallEndY - wallThickness})");

            sb.AppendLine();
            sb.AppendLine("### 建造顺序");
            sb.AppendLine("1. designate_room 建造内层9个房间（任意顺序）");
            sb.AppendLine("2. designate_room 建造4段外围墙（北→南→西→东，不设门）");

            return ToolResult.Success(sb.ToString().TrimEnd());
        }

        // ── bedroom_row ────────────────────────────────────────────────
        private static ToolResult BuildBedroomRow(int cx, int cy, JsonElement? options)
        {
            int count = 5;
            int internalW = 5;
            int internalH = 5;

            if (options != null)
            {
                if (options.Value.TryGetProperty("count", out var jCt) && jCt.TryGetInt32(out var ct)) count = ct;
                if (options.Value.TryGetProperty("internal_width", out var jIw) && jIw.TryGetInt32(out var iw)) internalW = iw;
                if (options.Value.TryGetProperty("internal_height", out var jIh) && jIh.TryGetInt32(out var ih)) internalH = ih;
            }

            int stride = internalW + 1;              // +1 = shared wall
            int rowWidth = count * stride + 1;       // +1 = first room's left wall
            int rowHeight = internalH + 2;           // +2 = top + bottom walls

            int originX = cx - rowWidth / 2;
            int originY = cy - rowHeight / 2;

            var sb = new StringBuilder();
            sb.AppendLine($"## 模板: bedroom_row (中心: {cx}, {cy})");
            sb.AppendLine();
            sb.AppendLine($"{count} 间卧室，内径 {internalW}×{internalH}，共用墙，总占地 {rowWidth}×{rowHeight}");
            sb.AppendLine();

            sb.AppendLine("### 房间");
            for (int i = 0; i < count; i++)
            {
                int px = originX + i * stride;
                int py = originY;
                int ex = px + internalW + 1;
                int ey = py + internalH + 1;
                int area = internalW * internalH;
                sb.AppendLine($"[{i}] 卧室{i + 1} — pos=({px},{py}) end=({ex},{ey}) 内部{area}格 建议门: bottom");
            }

            sb.AppendLine();
            sb.AppendLine("### 建造顺序");
            sb.AppendLine($"designate_room 从左到右逐一建造 {count} 间卧室（共用墙自动跳过）");

            return ToolResult.Success(sb.ToString().TrimEnd());
        }
    }
}
