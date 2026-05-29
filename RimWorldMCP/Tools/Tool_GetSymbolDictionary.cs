using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using RimWorldMCP.MapRendering;

namespace RimWorldMCP.Tools
{
    public class Tool_GetSymbolDictionary : ITool
    {
        public string Name => "get_symbol_dictionary";
        public string Description => "获取 Def→显示符号映射字典（正向/反向/按字符过滤）";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                mode = new
                {
                    type = "string",
                    @enum = new[] { "all", "forward", "reverse", "by_chars" },
                    description = "查询模式: all=全部, forward=正向查DefName, reverse=反向查字符, by_chars=按字符数组过滤"
                },
                chars = new
                {
                    type = "array",
                    items = new { type = "string", minLength = 1, maxLength = 1 },
                    description = "当 mode=by_chars 时，指定要查询的字符列表"
                },
                def_name = new
                {
                    type = "string",
                    description = "当 mode=forward 时，指定要查询的单个 defName"
                },
                symbol = new
                {
                    type = "string", minLength = 1, maxLength = 1,
                    description = "当 mode=reverse 时，指定要反向查询的单个字符"
                }
            }
        });

        public Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null)
                return Task.FromResult(ToolResult.Error("缺少参数"));

            string mode = "all";
            if (args.Value.TryGetProperty("mode", out var jMode))
                mode = jMode.GetString() ?? "all";

            var sb = new StringBuilder();
            sb.AppendLine($"## 符号字典 (条目: {SymbolDictionary.EntryCount})  Hash: {SymbolDictionary.DictHash}");
            sb.AppendLine();

            switch (mode)
            {
                case "forward":
                {
                    if (!args.Value.TryGetProperty("def_name", out var jDef) ||
                        string.IsNullOrEmpty(jDef.GetString()))
                        return Task.FromResult(ToolResult.Error("mode=forward 时需要 def_name 参数"));

                    var defName = jDef.GetString()!;
                    char c = SymbolDictionary.GetChar(defName);
                    if (c == '?')
                    {
                        sb.AppendLine($"未找到 defName: {defName}");
                    }
                    else
                    {
                        var info = SymbolDictionary.Lookup(c);
                        sb.AppendLine($"{c} = {info}");
                    }
                    break;
                }

                case "reverse":
                {
                    if (!args.Value.TryGetProperty("symbol", out var jSym) ||
                        string.IsNullOrEmpty(jSym.GetString()))
                        return Task.FromResult(ToolResult.Error("mode=reverse 时需要 symbol 参数"));

                    var sym = jSym.GetString()!;
                    if (sym.Length > 0)
                    {
                        var info = SymbolDictionary.Lookup(sym[0]);
                        sb.AppendLine($"{sym[0]} = {info}");
                    }
                    break;
                }

                case "by_chars":
                {
                    if (!args.Value.TryGetProperty("chars", out var jChars) ||
                        jChars.ValueKind != JsonValueKind.Array)
                        return Task.FromResult(ToolResult.Error("mode=by_chars 时需要 chars 参数(字符数组)"));

                    var chars = new List<char>();
                    foreach (var element in jChars.EnumerateArray())
                    {
                        var s = element.GetString();
                        if (!string.IsNullOrEmpty(s))
                            chars.Add(s[0]);
                    }

                    var filtered = SymbolDictionary.GetByChars(chars);
                    foreach (var kv in filtered.OrderBy(x => x.Key))
                        sb.AppendLine($"{kv.Key} {kv.Value.Label}");
                    break;
                }

                default: // "all"
                {
                    var all = SymbolDictionary.GetAll();
                    // 按分类分组输出
                    var byCategory = all.GroupBy(kv => kv.Value.Category)
                        .OrderBy(g => g.Key);
                    foreach (var group in byCategory)
                    {
                        sb.AppendLine($"## {group.Key}");
                        foreach (var kv in group.OrderBy(x => x.Key))
                            sb.AppendLine($"{kv.Key} {kv.Value.Label}");
                        sb.AppendLine();
                    }
                    break;
                }
            }

            return Task.FromResult(ToolResult.Success(sb.ToString().TrimEnd()));
        }

        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args)
        {
            return null;
        }
    }
}
