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

        // Dialog_Confirm 私有字段
        private static readonly FieldInfo? ConfirmTitleField =
            typeof(Dialog_Confirm).GetField("title", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo? ConfirmButtonField =
            typeof(Dialog_Confirm).GetField("confirm", BindingFlags.Instance | BindingFlags.NonPublic);

        // Dialog_Slider 私有字段
        private static readonly FieldInfo? SliderCurValueField =
            typeof(Dialog_Slider).GetField("curValue", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo? SliderTextGetterField =
            typeof(Dialog_Slider).GetField("textGetter", BindingFlags.Instance | BindingFlags.Public);

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
                                if (curNode == null)
                                {
                                    sb.AppendLine();
                                    sb.AppendLine($"## 弹框 [{dialogIdx}] {w.GetType().Name} (事件未加载)");
                                    dialogIdx++;
                                    continue;
                                }

                                var nodeTextProp = curNode.GetType().GetProperty("text", BindingFlags.Instance | BindingFlags.Public);
                                var nodeText = nodeTextProp?.GetValue(curNode) as string ?? "";

                                // options 是 public field，不是 property
                                var optionsField = curNode.GetType().GetField("options", BindingFlags.Instance | BindingFlags.Public);
                                var diaOptions = optionsField?.GetValue(curNode) as System.Collections.IList;

                                sb.AppendLine();
                                sb.AppendLine($"## 弹框 [{dialogIdx}] {w.GetType().Name}");
                                if (!string.IsNullOrEmpty(nodeText))
                                    sb.AppendLine($"正文: {nodeText}");

                                if (diaOptions == null || diaOptions.Count == 0)
                                {
                                    sb.AppendLine("(没有可用选项)");
                                }
                                else
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
                            catch (Exception ex)
                            {
                                sb.AppendLine();
                                sb.AppendLine($"## 弹框 [{dialogIdx}] {w.GetType().Name} (解析异常)");
                                sb.AppendLine($"(错误: {ex.Message})");
                                dialogIdx++;
                            }
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
                        else if (w is Dialog_Confirm)
                        {
                            var title = ConfirmTitleField?.GetValue(w) as string ?? "";
                            var confirmBtn = ConfirmButtonField?.GetValue(w) as string ?? "确认";

                            sb.AppendLine();
                            sb.AppendLine($"## 弹框 [{dialogIdx}] 确认对话框 ({w.GetType().Name})");
                            if (!string.IsNullOrEmpty(title))
                                sb.AppendLine($"内容: {title}");
                            sb.AppendLine($"[0] {confirmBtn}");
                            sb.AppendLine($"[1] 取消");
                            dialogIdx++;
                        }
                        else if (w is Dialog_Slider slider)
                        {
                            var curValue = (int)(SliderCurValueField?.GetValue(w) ?? 0);
                            var textGetter = SliderTextGetterField?.GetValue(w) as Func<int, string>;
                            var label = textGetter?.Invoke(curValue) ?? "";

                            sb.AppendLine();
                            sb.AppendLine($"## 弹框 [{dialogIdx}] 滑动条 ({w.GetType().Name})");
                            if (!string.IsNullOrEmpty(label))
                                sb.AppendLine($"说明: {label}");
                            sb.AppendLine($"范围: {slider.from} ~ {slider.to}");
                            sb.AppendLine($"当前值: {curValue}");
                            sb.AppendLine($"[0] 确认 ({curValue})");
                            sb.AppendLine($"[1] 取消");
                            dialogIdx++;
                        }
                        else
                        {
                            bool canClose = w.doCloseX || w.doCloseButton || w.closeOnClickedOutside
                                         || w.closeOnAccept || w.closeOnCancel;
                            sb.AppendLine();
                            if (canClose)
                                sb.AppendLine($"## 弹框 [{dialogIdx}] {w.GetType().Name}\n[0] 关闭");
                            else
                                sb.AppendLine($"## 弹框 [{dialogIdx}] {w.GetType().Name} (无法关闭，需手动处理)");
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
        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args) => null;
    }
}
