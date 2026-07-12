using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using RimWorldAgent.IPC.Generated;

namespace RimWorldAgent.Core.AgentTransport
{
    /// <summary>ACP Session Config Options 的解析、序列化与应用辅助。</summary>
    internal static class AcpSessionConfig
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        public static string SerializeOptions(IReadOnlyList<SessionConfigOptionDto>? options)
        {
            if (options == null || options.Count == 0) return "[]";
            return JsonSerializer.Serialize(options, JsonOptions);
        }

        public static List<SessionConfigOptionDto> DeserializeOptions(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new List<SessionConfigOptionDto>();
            try
            {
                return JsonSerializer.Deserialize<List<SessionConfigOptionDto>>(json!, JsonOptions)
                    ?? new List<SessionConfigOptionDto>();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[AcpSessionConfig] DeserializeOptions failed: {ex.GetType().Name}: {ex.Message}");
                return new List<SessionConfigOptionDto>();
            }
        }

        public static List<AcpSessionConfigSelectionValue> SnapshotSelections(IReadOnlyList<SessionConfigOptionDto>? options)
        {
            var result = new List<AcpSessionConfigSelectionValue>();
            if (options == null) return result;
            foreach (var option in options)
            {
                if (option == null || string.IsNullOrWhiteSpace(option.Id)) continue;
                var type = string.IsNullOrWhiteSpace(option.Type) ? "select" : option.Type.Trim();
                if (!string.Equals(type, "select", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(type, "boolean", StringComparison.OrdinalIgnoreCase))
                    continue;
                result.Add(new AcpSessionConfigSelectionValue
                {
                    ConfigId = option.Id.Trim(),
                    Type = type.ToLowerInvariant(),
                    Value = CurrentValueToString(option)
                });
            }
            return result;
        }

        /// <summary>
        /// 为探测会话构造一次性的配置应用序列。已有保存值优先；初始目录中没有保存值的项使用
        /// agent 返回的 currentValue。目录暂未出现的保存项保留在尾部，等待父配置应用后再尝试。
        /// </summary>
        public static List<AcpSessionConfigSelectionValue> BuildProbeSelections(
            IReadOnlyList<SessionConfigOptionDto>? options,
            IReadOnlyList<AcpSessionConfigSelectionValue>? savedSelections)
        {
            var savedById = new Dictionary<string, AcpSessionConfigSelectionValue>(StringComparer.Ordinal);
            var savedOrder = new List<AcpSessionConfigSelectionValue>();
            foreach (var selection in savedSelections ?? Array.Empty<AcpSessionConfigSelectionValue>())
            {
                if (selection == null || string.IsNullOrWhiteSpace(selection.ConfigId)) continue;
                var normalized = new AcpSessionConfigSelectionValue
                {
                    ConfigId = selection.ConfigId.Trim(),
                    Type = string.IsNullOrWhiteSpace(selection.Type) ? "select" : selection.Type.Trim(),
                    Value = selection.Value ?? ""
                };
                if (savedById.ContainsKey(normalized.ConfigId)) continue;
                savedById.Add(normalized.ConfigId, normalized);
                savedOrder.Add(normalized);
            }

            var result = new List<AcpSessionConfigSelectionValue>();
            var included = new HashSet<string>(StringComparer.Ordinal);
            foreach (var option in options ?? Array.Empty<SessionConfigOptionDto>())
            {
                if (option == null || string.IsNullOrWhiteSpace(option.Id)) continue;
                var type = string.IsNullOrWhiteSpace(option.Type) ? "select" : option.Type.Trim();
                if (!string.Equals(type, "select", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(type, "boolean", StringComparison.OrdinalIgnoreCase))
                    continue;

                var selection = new AcpSessionConfigSelectionValue
                {
                    ConfigId = option.Id.Trim(),
                    Type = type,
                    Value = CurrentValueToString(option)
                };
                if (savedById.TryGetValue(selection.ConfigId, out var saved)
                    && IsSelectionApplicable(option, saved, out _))
                {
                    selection.Type = saved.Type;
                    selection.Value = saved.Value;
                }
                result.Add(selection);
                included.Add(selection.ConfigId);
            }

            foreach (var saved in savedOrder)
            {
                if (included.Contains(saved.ConfigId)) continue;
                result.Add(saved);
            }
            return result;
        }

        public static string CurrentValueToString(SessionConfigOptionDto option)
        {
            if (option.CurrentValue == null || option.CurrentValue.Value.ValueKind == JsonValueKind.Undefined
                || option.CurrentValue.Value.ValueKind == JsonValueKind.Null)
                return "";
            var value = option.CurrentValue.Value;
            if (value.ValueKind == JsonValueKind.True) return "true";
            if (value.ValueKind == JsonValueKind.False) return "false";
            if (value.ValueKind == JsonValueKind.String) return value.GetString() ?? "";
            return value.ToString();
        }

        public static JsonElement ValueToJsonElement(string type, string value)
        {
            if (string.Equals(type, "boolean", StringComparison.OrdinalIgnoreCase))
            {
                var b = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
                return JsonSerializer.SerializeToElement(b);
            }
            return JsonSerializer.SerializeToElement(value ?? "");
        }

        public static bool TryGetSelectValues(SessionConfigOptionDto option, out List<(string Value, string Name, string? Description)> values)
        {
            values = new List<(string, string, string?)>();
            if (option.Options == null) return false;
            foreach (var item in option.Options)
            {
                FlattenSelectOption(item, values);
            }
            return values.Count > 0;
        }

        private static void FlattenSelectOption(JsonElement item, List<(string Value, string Name, string? Description)> values)
        {
            if (item.ValueKind != JsonValueKind.Object) return;
            if (item.TryGetProperty("group", out _) && item.TryGetProperty("options", out var nested)
                && nested.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in nested.EnumerateArray())
                    FlattenSelectOption(child, values);
                return;
            }
            if (!item.TryGetProperty("value", out var valueEl)) return;
            var value = valueEl.ValueKind == JsonValueKind.String ? valueEl.GetString() ?? "" : valueEl.ToString();
            if (string.IsNullOrEmpty(value)) return;
            var name = value;
            if (item.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                name = nameEl.GetString() ?? value;
            string? description = null;
            if (item.TryGetProperty("description", out var descEl) && descEl.ValueKind == JsonValueKind.String)
                description = descEl.GetString();
            values.Add((value, name, description));
        }

        public static SessionConfigOptionDto? FindOption(IReadOnlyList<SessionConfigOptionDto>? options, string configId)
        {
            if (options == null || string.IsNullOrWhiteSpace(configId)) return null;
            return options.FirstOrDefault(o => o != null
                && string.Equals(o.Id, configId, StringComparison.Ordinal));
        }

        public static bool IsSelectionApplicable(SessionConfigOptionDto option, AcpSessionConfigSelectionValue selection, out string reason)
        {
            reason = "";
            if (option == null)
            {
                reason = "option missing";
                return false;
            }

            var optionType = string.IsNullOrWhiteSpace(option.Type) ? "select" : option.Type.Trim();
            var selectionType = string.IsNullOrWhiteSpace(selection.Type) ? optionType : selection.Type.Trim();
            if (!string.Equals(optionType, selectionType, StringComparison.OrdinalIgnoreCase))
            {
                reason = $"type mismatch option={optionType} selection={selectionType}";
                return false;
            }

            if (string.Equals(optionType, "boolean", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(selection.Value, "true", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(selection.Value, "false", StringComparison.OrdinalIgnoreCase))
                {
                    reason = "boolean value invalid";
                    return false;
                }
                return true;
            }

            if (string.Equals(optionType, "select", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryGetSelectValues(option, out var values))
                {
                    reason = "select options empty";
                    return false;
                }
                if (!values.Any(v => string.Equals(v.Value, selection.Value, StringComparison.Ordinal)))
                {
                    reason = "value not in options";
                    return false;
                }
                return true;
            }

            reason = $"unsupported type {optionType}";
            return false;
        }
    }
}
