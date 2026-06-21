using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RimWorldAgent.Core.AgentRuntime
{
    public static class ToolResultCacheKey
    {
        public static string Build(string sessionId, string toolName, IReadOnlyDictionary<string, JsonElement> args)
        {
            if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(toolName))
                return "";

            var inputHash = Hash(Canonicalize(args));
            return $"{sessionId}:{toolName}:{inputHash}";
        }

        public static string Canonicalize(IReadOnlyDictionary<string, JsonElement> args)
        {
            var sb = new StringBuilder();
            sb.Append('{');
            var first = true;
            foreach (var pair in args.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append(JsonSerializer.Serialize(pair.Key));
                sb.Append(':');
                AppendCanonicalJson(sb, pair.Value);
            }
            sb.Append('}');
            return sb.ToString();
        }

        private static string Hash(string text)
        {
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? ""));
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes)
                sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        private static void AppendCanonicalJson(StringBuilder sb, JsonElement value)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.Object:
                    sb.Append('{');
                    var firstObjectProperty = true;
                    foreach (var property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                    {
                        if (!firstObjectProperty) sb.Append(',');
                        firstObjectProperty = false;
                        sb.Append(JsonSerializer.Serialize(property.Name));
                        sb.Append(':');
                        AppendCanonicalJson(sb, property.Value);
                    }
                    sb.Append('}');
                    break;

                case JsonValueKind.Array:
                    sb.Append('[');
                    var firstArrayItem = true;
                    foreach (var item in value.EnumerateArray())
                    {
                        if (!firstArrayItem) sb.Append(',');
                        firstArrayItem = false;
                        AppendCanonicalJson(sb, item);
                    }
                    sb.Append(']');
                    break;

                case JsonValueKind.String:
                    sb.Append(JsonSerializer.Serialize(value.GetString()));
                    break;

                case JsonValueKind.Number:
                case JsonValueKind.True:
                case JsonValueKind.False:
                case JsonValueKind.Null:
                    sb.Append(value.GetRawText());
                    break;

                default:
                    sb.Append(value.GetRawText());
                    break;
            }
        }
    }
}
