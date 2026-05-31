using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;

namespace RimWorldMCP.Tools
{
    public class Tool_DismissNotification : ITool, INoMapRequired
    {
        public string Name => "dismiss_notification";
        public string Description => "关闭/清除游戏内的信封通知。可指定 letter_id 关闭单个，或用 type 批量关闭（如 positive、negative、threat、all 等）。";

        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args) => null;

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                letter_id = new { type = "integer", description = "可选。要关闭的信封 ID（来自 get_notifications 的 ID 字段）" },
                type = new { type = "string", description = "可选。批量关闭类型: positive(正面), negative(负面), threat(威胁), death(死亡), neutral(事件), all(全部可关闭)" }
            }
        });

        private static readonly Dictionary<string, string[]> TypeMap = new()
        {
            { "positive", new[] { "PositiveEvent", "AcceptVisitors", "AcceptJoiner", "AcceptCreepJoiner", "BabyBirth", "BabyToChild", "ChildToAdult", "ChildBirthday", "RitualOutcomePositive" } },
            { "negative", new[] { "NegativeEvent", "RitualOutcomeNegative", "ChoosePawn", "GameEnded", "BundleLetter" } },
            { "threat", new[] { "ThreatBig", "ThreatSmall", "Bossgroup" } },
            { "death", new[] { "Death" } },
            { "neutral", new[] { "NeutralEvent", "RelicHuntInstallationFound", "EntityDiscovered" } },
        };

        public Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            var letters = Find.LetterStack.LettersListForReading;
            if (letters.Count == 0)
                return Task.FromResult(ToolResult.Success("当前没有信封通知可关闭。"));

            JsonElement idEl = default, typeEl = default;
            var hasId = args?.TryGetProperty("letter_id", out idEl) == true;
            var hasType = args?.TryGetProperty("type", out typeEl) == true;

            if (!hasId && !hasType)
                return Task.FromResult(ToolResult.Error("请指定 letter_id 或 type 参数。推荐先调 get_notifications 查看有哪些通知。"));

            int closed = 0;
            var toRemove = new List<Letter>();

            if (hasId)
            {
                var id = idEl.GetInt32();
                foreach (var let in letters)
                    if (let.ID == id) { toRemove.Add(let); break; }
                if (toRemove.Count == 0)
                    return Task.FromResult(ToolResult.Error($"未找到 ID={id} 的信封通知。请用 get_notifications 获取最新列表。"));
            }
            else if (hasType)
            {
                var type = typeEl.GetString()?.Trim().ToLowerInvariant() ?? "";
                if (type == "all")
                {
                    toRemove.AddRange(letters.Where(let => let.CanDismissWithRightClick));
                }
                else if (TypeMap.TryGetValue(type, out var defNames))
                {
                    var defNameSet = new HashSet<string>(defNames);
                    toRemove.AddRange(letters.Where(let => defNameSet.Contains(let.def.defName) && let.CanDismissWithRightClick));
                }
                else
                {
                    return Task.FromResult(ToolResult.Error($"未知类型: {type}。支持: positive, negative, threat, death, neutral, all"));
                }
            }

            foreach (var let in toRemove)
            {
                try
                {
                    Find.LetterStack.RemoveLetter(let);
                    closed++;
                }
                catch (Exception ex)
                {
                    Log.Warning($"[dismiss_notification] 关闭信件 ID={let.ID} 失败: {ex.Message}");
                }
            }

            return Task.FromResult(ToolResult.Success(closed > 0
                ? $"已关闭 {closed} 个信封通知。"
                : "没有符合条件的信封通知可关闭。"));
        }
    }
}
