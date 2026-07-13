using System;
using System.Text.Json;

namespace RimWorldAgent.Core.AgentTransport
{
    /// <summary>
    /// ACP session _meta JSON 校验/归一化。
    /// 空串表示不传 _meta；非空必须是 JSON object。
    /// </summary>
    internal static class AcpSessionMetaJson
    {
        public static bool TryValidate(string? text, out string error)
        {
            error = "";
            if (string.IsNullOrWhiteSpace(text)) return true;

            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    error = "Session _meta 必须是 JSON object（不能是数组或标量）。";
                    return false;
                }
                return true;
            }
            catch (JsonException ex)
            {
                error = "Session _meta JSON 无效: " + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// 空串 => ""。非空必须是 JSON object，否则抛 InvalidOperationException。
        /// 返回紧凑 JSON 文本。
        /// </summary>
        public static string Normalize(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            if (!TryValidate(text, out var error))
                throw new InvalidOperationException(error);

            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.GetRawText();
        }
    }
}
