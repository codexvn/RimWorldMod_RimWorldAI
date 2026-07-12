using System;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace RimWorldAgent.Core.AgentTransport
{
    /// <summary>
    /// 在 C# 侧根据 Backend 配置的 JsonPath + 正则，对 ACP requestPermission 原始 JSON 做权限判定。
    /// 权限匹配的是工具网关名（title），不是 UI 展示用的 action 名。
    /// Codex 的 permission 请求常不带 toolCall.title，此时用 toolCallId 反查 projector 缓存的 title。
    /// </summary>
    internal static class AcpToolPermissionEvaluator
    {
        public static bool IsAllowed(
            object permissionParams,
            string toolNameJsonPath,
            string allowedToolRegex,
            string fallbackTitle,
            out string toolName,
            out string detail)
        {
            toolName = "";
            detail = "";
            try
            {
                var root = permissionParams is JToken token
                    ? token
                    : JToken.FromObject(permissionParams ?? new { });

                var namePath = string.IsNullOrWhiteSpace(toolNameJsonPath) ? "$.toolCall.title" : toolNameJsonPath.Trim();
                var selected = root.SelectToken(namePath);
                string extractDetail;
                if (selected != null && selected.Type != JTokenType.Null)
                {
                    toolName = TokenToDisplayString(selected);
                    extractDetail = "path=" + namePath;
                }
                else if (!string.IsNullOrWhiteSpace(fallbackTitle))
                {
                    // JsonPath 未命中，用 toolCallId 反查到的 title
                    toolName = fallbackTitle;
                    extractDetail = "fallback=toolCallId->title(\"" + fallbackTitle + "\")";
                }
                else
                {
                    var available = SummarizeAvailablePaths(root);
                    detail = "JsonPath 未命中: " + namePath + "；fallback 无缓存 title；可用字段=" + available;
                    return false;
                }

                if (toolName.Length == 0)
                {
                    detail = "工具名为空: path=" + namePath + "；fallback=\"" + fallbackTitle + "\"";
                    return false;
                }

                var pattern = string.IsNullOrWhiteSpace(allowedToolRegex) ? "^mcp" : allowedToolRegex.Trim();
                var optionKinds = SummarizeOptionKinds(root);
                try
                {
                    var allowed = Regex.IsMatch(toolName, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                    detail = allowed
                        ? $"允许: 工具名 \"{toolName}\" 匹配 {pattern}（{extractDetail}; options={optionKinds}）"
                        : $"拒绝: 工具名 \"{toolName}\" 不匹配 {pattern}（{extractDetail}; options={optionKinds}）";
                    return allowed;
                }
                catch (Exception ex)
                {
                    var fallback = toolName.StartsWith("mcp", StringComparison.OrdinalIgnoreCase);
                    detail = $"正则无效({ex.GetType().Name}: {ex.Message})，回退 ^mcp => {(fallback ? "允许" : "拒绝")}; 工具名=\"{toolName}\"";
                    return fallback;
                }
            }
            catch (Exception ex)
            {
                detail = $"判定异常: {ex.GetType().Name}: {ex.Message}";
                return false;
            }
        }

        public static object BuildSelectedOutcome(object permissionParams, bool allow, out string outcomeDetail)
        {
            outcomeDetail = "";
            var root = permissionParams is JToken token
                ? token
                : JToken.FromObject(permissionParams ?? new { });
            var options = root["options"] as JArray;
            if (options == null)
            {
                outcomeDetail = "无 options，返回 cancelled";
                return new { outcome = new { outcome = "cancelled" } };
            }

            JObject? pick = null;
            if (allow)
            {
                pick = options.OfType<JObject>().FirstOrDefault(o => string.Equals(o.Value<string>("kind"), "allow_always", StringComparison.OrdinalIgnoreCase))
                    ?? options.OfType<JObject>().FirstOrDefault(o => string.Equals(o.Value<string>("kind"), "allow_once", StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                pick = options.OfType<JObject>().FirstOrDefault(o =>
                {
                    var kind = o.Value<string>("kind") ?? "";
                    return kind.StartsWith("reject", StringComparison.OrdinalIgnoreCase);
                });
            }

            if (pick == null)
            {
                outcomeDetail = allow
                    ? "未找到 allow_once/allow_always，返回 cancelled"
                    : "未找到 reject_*，返回 cancelled";
                return new { outcome = new { outcome = "cancelled" } };
            }

            var optionId = pick.Value<string>("optionId") ?? "";
            var kindName = pick.Value<string>("kind") ?? "";
            outcomeDetail = $"selected kind={kindName} optionId={optionId}";
            return new { outcome = new { outcome = "selected", optionId } };
        }

        private static string SummarizeOptionKinds(JToken root)
        {
            var options = root["options"] as JArray;
            if (options == null) return "<none>";
            var kinds = options.OfType<JObject>()
                .Select(o =>
                {
                    var kind = o.Value<string>("kind") ?? "";
                    var optionId = o.Value<string>("optionId") ?? "";
                    if (kind.Length == 0 && optionId.Length == 0) return null;
                    return string.IsNullOrEmpty(optionId) ? kind : (kind + ":" + optionId);
                })
                .Where(x => x != null)
                .ToList();
            return kinds.Count == 0 ? "<empty>" : string.Join(",", kinds);
        }

        /// <summary>JsonPath 未命中且无 fallback 时，列出顶层/toolCall 关键字段。</summary>
        private static string SummarizeAvailablePaths(JToken root)
        {
            var parts = new System.Collections.Generic.List<string>();
            if (root is JObject obj)
                parts.Add("root=[" + string.Join(",", obj.Properties().Select(p => p.Name)) + "]");

            var toolCall = root["toolCall"] as JObject;
            if (toolCall != null)
                parts.Add("toolCall=[" + string.Join(",", toolCall.Properties().Select(p => p.Name)) + "]");

            return parts.Count == 0 ? "<empty>" : string.Join("; ", parts);
        }

        private static string TokenToDisplayString(JToken token, int maxLen = 0)
        {
            string text;
            if (token.Type == JTokenType.String
                || token.Type == JTokenType.Integer
                || token.Type == JTokenType.Float
                || token.Type == JTokenType.Boolean)
            {
                text = token.ToString() ?? "";
            }
            else
            {
                text = token.ToString(Newtonsoft.Json.Formatting.None) ?? "";
            }

            text = text.Trim();
            if (maxLen > 0 && text.Length > maxLen)
                return text.Substring(0, maxLen) + "...";
            return text;
        }
    }
}
