using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using RimWorld;

namespace RimWorldMCP.Tools
{
    public class Tool_GetOpenDialogs : ITool
    {
        public string Name => "get_open_dialogs";
        public string Description => "列出当前游戏内打开的弹框/对话框/右键菜单的所有选项。AI 可据此用 select_dialog_option 选择。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { }
        });

        // DiaOption.text 是 protected, Activate() 是 protected
        private static readonly FieldInfo? DiaOptionTextField =
            typeof(DiaOption).GetField("text", BindingFlags.Instance | BindingFlags.NonPublic);

        // Dialog_GiveName protected 字段
        private static readonly FieldInfo? GiveNameCurNameField =
            typeof(Dialog_GiveName).GetField("curName", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo? GiveNameCurSecondNameField =
            typeof(Dialog_GiveName).GetField("curSecondName", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo? GiveNameUseSecondField =
            typeof(Dialog_GiveName).GetField("useSecondName", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo? GiveNameMsgKeyField =
            typeof(Dialog_GiveName).GetField("nameMessageKey", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo? GiveNamePawnField =
            typeof(Dialog_GiveName).GetField("suggestingPawn", BindingFlags.Instance | BindingFlags.NonPublic);

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    var stack = Find.WindowStack;
                    if (stack == null) return ToolResult.Error("WindowStack 不可用");

                    var windows = stack.Windows;
                    if (windows == null || windows.Count == 0)
                        return ToolResult.Success("当前没有打开的弹框。");

                    var sb = new StringBuilder();
                    int dialogIdx = 0;

                    var dialogs = RimWorldMCP.Helpers.DialogHelper.GetInteractableDialogs();

                    foreach (var w in dialogs)
                    {
                        if (w is FloatMenu fm)
                        {
                            var options = RimWorldMCP.Helpers.DialogHelper.FloatMenuOptionsField?.GetValue(fm) as List<FloatMenuOption>;
                            if (options == null || options.Count == 0) continue;

                            sb.AppendLine();
                            sb.AppendLine($"## 弹框 [{dialogIdx}] FloatMenu ({options.Count} 项)");
                            for (int i = 0; i < options.Count; i++)
                            {
                                var opt = options[i];
                                string mark = opt.Disabled ? " [禁用]" : "";
                                if (opt.Disabled)
                                    sb.AppendLine($"[{i}] {opt.Label}{mark}");
                                else
                                    sb.AppendLine($"[{i}] {opt.Label}");
                            }
                            dialogIdx++;
                        }
                    }

                    foreach (var w in dialogs)
                    {
                        if (w is FloatMenu) continue;

                        if (w is Dialog_MessageBox msgBox)
                        {
                            sb.AppendLine();
                            sb.AppendLine($"## 弹框 [{dialogIdx}] 确认对话框");
                            if (!string.IsNullOrEmpty(msgBox.title))
                                sb.AppendLine($"标题: {msgBox.title}");
                            if (!msgBox.text.NullOrEmpty())
                                sb.AppendLine($"正文: {msgBox.text}");

                            // 按钮从左到右: B, C, A
                            int btnIdx = 0;
                            if (!string.IsNullOrEmpty(msgBox.buttonBText))
                            {
                                sb.AppendLine($"[{btnIdx}] {msgBox.buttonBText}");
                                btnIdx++;
                            }
                            if (!string.IsNullOrEmpty(msgBox.buttonCText))
                            {
                                sb.AppendLine($"[{btnIdx}] {msgBox.buttonCText}");
                                btnIdx++;
                            }
                            if (!string.IsNullOrEmpty(msgBox.buttonAText))
                            {
                                sb.AppendLine($"[{btnIdx}] {msgBox.buttonAText}{(msgBox.buttonADestructive ? " ⚠" : "")}");
                                btnIdx++;
                            }
                            dialogIdx++;
                        }
                        else if (w is Dialog_NodeTree)  // 匹配基类，子类也会匹配
                        {
                            try
                            {
                                // curNode 是 protected 字段 (field)，定义在基类 Dialog_NodeTree 中
                                // .NET 的 GetField 对 NonPublic 字段不搜索继承链，需手动遍历基类
                                Type curType = w.GetType();
                                FieldInfo? curNodeField = null;
                                while (curType != null && curNodeField == null)
                                {
                                    curNodeField = curType.GetField("curNode", BindingFlags.Instance | BindingFlags.NonPublic);
                                    curType = curType.BaseType;
                                }
                                var curNode = curNodeField?.GetValue(w);
                                if (curNode == null) continue;

                                var nodeTextProp = curNode.GetType().GetProperty("text", BindingFlags.Instance | BindingFlags.Public);
                                var nodeText = nodeTextProp?.GetValue(curNode) as string ?? "";

                                var optionsProp = curNode.GetType().GetProperty("options", BindingFlags.Instance | BindingFlags.Public);
                                var diaOptions = optionsProp?.GetValue(curNode) as System.Collections.IList;

                                sb.AppendLine();
                                sb.AppendLine($"## 弹框 [{dialogIdx}] 事件选项");
                                if (!string.IsNullOrEmpty(nodeText))
                                    sb.AppendLine($"正文: {nodeText}");

                                if (diaOptions != null)
                                {
                                    for (int i = 0; i < diaOptions.Count; i++)
                                    {
                                        var opt = diaOptions[i];
                                        string label = DiaOptionTextField?.GetValue(opt) as string ?? $"?{i}";
                                        bool disabled = (bool)(opt.GetType().GetField("disabled", BindingFlags.Instance | BindingFlags.Public)?.GetValue(opt) ?? false);
                                        string reason = disabled
                                            ? (opt.GetType().GetField("disabledReason", BindingFlags.Instance | BindingFlags.Public)?.GetValue(opt) as string ?? "禁用")
                                            : "";
                                        if (disabled)
                                            sb.AppendLine($"[{i}] {label} [禁用: {reason}]");
                                        else
                                            sb.AppendLine($"[{i}] {label}");
                                    }
                                }
                                dialogIdx++;
                            }
                            catch { /* skip malfunctioning node tree */ }
                        }
                        else if (w is Dialog_GiveName giveName)
                        {
                            var curName = GiveNameCurNameField?.GetValue(w) as string ?? "";
                            var useSecond = (bool)(GiveNameUseSecondField?.GetValue(w) ?? false);
                            var curSecondName = useSecond ? (GiveNameCurSecondNameField?.GetValue(w) as string ?? "") : null;
                            var msgKey = GiveNameMsgKeyField?.GetValue(w) as string ?? "";
                            var pawn = GiveNamePawnField?.GetValue(w) as Pawn;
                            var label = pawn?.LabelShort ?? "";

                            string prompt;
                            try { prompt = string.IsNullOrEmpty(msgKey) ? "" : msgKey.Translate(label, pawn).CapitalizeFirst(); }
                            catch { prompt = msgKey; }

                            sb.AppendLine();
                            sb.AppendLine($"## 弹框 [{dialogIdx}] 命名 ({w.GetType().Name})");
                            if (!string.IsNullOrEmpty(prompt))
                                sb.AppendLine($"提示: {prompt}");
                            sb.AppendLine($"当前名称: {curName}");
                            if (curSecondName != null)
                                sb.AppendLine($"当前定居点名称: {curSecondName}");

                            sb.AppendLine($"[0] 确认 (使用当前名称)");
                            sb.AppendLine($"[1] 随机名称");
                            if (useSecond)
                                sb.AppendLine($"[2] 随机定居点名称");
                            dialogIdx++;
                        }
                        else
                        {
                            // 未知对话框类型
                            sb.AppendLine();
                            sb.AppendLine($"## 弹框 [{dialogIdx}] {w.GetType().Name} (不支持操作)");
                            dialogIdx++;
                        }
                    }

                    if (dialogIdx == 0)
                        return ToolResult.Success("当前没有可交互的弹框。");

                    return ToolResult.Success(sb.ToString().TrimStart());
                }
                catch (Exception ex)
                {
                    return ToolResult.Error($"获取弹框失败: {ex.Message}");
                }
            });
        }
        public (int x, int y)? GetTargetPos(JsonElement? args) => null;
    }
}
