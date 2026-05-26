using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using RimWorld;

namespace RimWorldMCP.Tools
{
    public class Tool_SelectDialogOption : ITool
    {
        public string Name => "select_dialog_option";
        public string Description => "选择弹框中的指定选项。先用 get_open_dialogs 获取弹框列表和选项编号。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                dialog_index = new { type = "integer", description = "弹框编号（来自 get_open_dialogs 输出的 [N]）" },
                option_index = new { type = "integer", description = "选项编号（来自选项列表的 [N]）" }
            },
            required = new[] { "dialog_index", "option_index" }
        });

        // DiaOption.Activate() 是 protected
        private static readonly MethodInfo? DiaOptionActivateMethod =
            typeof(DiaOption).GetMethod("Activate", BindingFlags.Instance | BindingFlags.NonPublic);

        // Dialog_GiveName protected 成员
        private static readonly FieldInfo? GiveNameCurNameField =
            typeof(Dialog_GiveName).GetField("curName", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo? GiveNameCurSecondNameField =
            typeof(Dialog_GiveName).GetField("curSecondName", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo? GiveNameUseSecondField =
            typeof(Dialog_GiveName).GetField("useSecondName", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo? GiveNameNameGenField =
            typeof(Dialog_GiveName).GetField("nameGenerator", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo? GiveNameSecondNameGenField =
            typeof(Dialog_GiveName).GetField("secondNameGenerator", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo? GiveNameIsValidNameMethod =
            typeof(Dialog_GiveName).GetMethod("IsValidName", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo? GiveNameIsValidSecondNameMethod =
            typeof(Dialog_GiveName).GetMethod("IsValidSecondName", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo? GiveNameNamedMethod =
            typeof(Dialog_GiveName).GetMethod("Named", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo? GiveNameNamedSecondMethod =
            typeof(Dialog_GiveName).GetMethod("NamedSecond", BindingFlags.Instance | BindingFlags.NonPublic);

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null) return ToolResult.Error("缺少参数");
            if (!args.Value.TryGetProperty("dialog_index", out var jDi) || !jDi.TryGetInt32(out var dialogIdx))
                return ToolResult.Error("缺少必填参数: dialog_index");
            if (!args.Value.TryGetProperty("option_index", out var jOi) || !jOi.TryGetInt32(out var optIdx))
                return ToolResult.Error("缺少必填参数: option_index");

            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    var dialogs = RimWorldMCP.Helpers.DialogHelper.GetInteractableDialogs();

                    // 遍历弹框，匹配 dialog_index
                    int curIdx = 0;

                    foreach (var w in dialogs)
                    {
                        if (w is FloatMenu fm)
                        {
                            if (curIdx != dialogIdx) { curIdx++; continue; }

                            var options = RimWorldMCP.Helpers.DialogHelper.FloatMenuOptionsField?.GetValue(fm) as List<FloatMenuOption>;
                            if (options == null || optIdx < 0 || optIdx >= options.Count)
                                return ToolResult.Error($"选项编号 {optIdx} 超出范围 (0~{(options?.Count ?? 0) - 1})");

                            var opt = options[optIdx];
                            if (opt.Disabled)
                                return ToolResult.Error($"选项 [{optIdx}] {opt.Label} 已被禁用，无法选择");

                            opt.Chosen(true, fm);
                            return ToolResult.Success($"已选择: {opt.Label}");
                        }
                    }

                    foreach (var w in dialogs)
                    {
                        if (w is FloatMenu) continue;

                        if (w is Dialog_MessageBox msgBox)
                        {
                            if (curIdx != dialogIdx) { curIdx++; continue; }

                            // 按钮顺序: B(0), C(1), A(2)
                            int btnIdx = 0;
                            if (!string.IsNullOrEmpty(msgBox.buttonBText))
                            {
                                if (btnIdx == optIdx) { msgBox.buttonBAction?.Invoke(); return ToolResult.Success($"已选择: {msgBox.buttonBText}"); }
                                btnIdx++;
                            }
                            if (!string.IsNullOrEmpty(msgBox.buttonCText))
                            {
                                if (btnIdx == optIdx) { msgBox.buttonCAction?.Invoke(); return ToolResult.Success($"已选择: {msgBox.buttonCText}"); }
                                btnIdx++;
                            }
                            if (!string.IsNullOrEmpty(msgBox.buttonAText))
                            {
                                if (btnIdx == optIdx) { msgBox.buttonAAction?.Invoke(); return ToolResult.Success($"已选择: {msgBox.buttonAText}"); }
                                btnIdx++;
                            }
                            return ToolResult.Error($"选项编号 {optIdx} 超出范围 (0~{btnIdx - 1})");
                        }

                        if (w is Dialog_NodeTree)
                        {
                            if (curIdx != dialogIdx) { curIdx++; continue; }

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
                            if (curNode == null) return ToolResult.Error("无法获取当前事件节点");

                            // options 是 public field，不是 property
                            var optionsField = curNode.GetType().GetField("options", BindingFlags.Instance | BindingFlags.Public);
                            var diaOptions = optionsField?.GetValue(curNode) as System.Collections.IList;
                            if (diaOptions == null || optIdx < 0 || optIdx >= diaOptions.Count)
                                return ToolResult.Error($"选项编号 {optIdx} 超出范围");

                            var opt = diaOptions[optIdx];
                            bool disabled = (bool)(opt.GetType().GetField("disabled", BindingFlags.Instance | BindingFlags.Public)?.GetValue(opt) ?? false);
                            if (disabled)
                            {
                                string reason = opt.GetType().GetField("disabledReason", BindingFlags.Instance | BindingFlags.Public)?.GetValue(opt) as string ?? "未知原因";
                                return ToolResult.Error($"选项 [{optIdx}] 已禁用: {reason}");
                            }

                            DiaOptionActivateMethod?.Invoke(opt, null);
                            string label = typeof(DiaOption).GetField("text", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(opt) as string ?? $"[{optIdx}]";
                            return ToolResult.Success($"已选择: {label}");
                        }

                        if (w is Dialog_GiveName giveNameInner)
                        {
                            if (curIdx != dialogIdx) { curIdx++; continue; }

                            var useSecond = (bool)(GiveNameUseSecondField?.GetValue(w) ?? false);
                            var maxOpt = useSecond ? 2 : 1;
                            if (optIdx < 0 || optIdx > maxOpt)
                                return ToolResult.Error($"选项编号 {optIdx} 超出范围 (0~{maxOpt})");

                            if (optIdx == 0)
                            {
                                var curName = GiveNameCurNameField?.GetValue(w) as string ?? "";
                                var curSecondName = useSecond ? (GiveNameCurSecondNameField?.GetValue(w) as string ?? "") : "";

                                var isValid = (bool)(GiveNameIsValidNameMethod?.Invoke(w, new object[] { curName }) ?? false);
                                if (!isValid)
                                    return ToolResult.Error($"名称 '{curName}' 不合法，请先随机一个名称");

                                if (useSecond)
                                {
                                    var isValidSecond = (bool)(GiveNameIsValidSecondNameMethod?.Invoke(w, new object[] { curSecondName }) ?? false);
                                    if (!isValidSecond)
                                        return ToolResult.Error($"定居点名称 '{curSecondName}' 不合法，请先随机一个名称");
                                }

                                GiveNameNamedMethod?.Invoke(w, new object[] { curName });
                                if (useSecond)
                                    GiveNameNamedSecondMethod?.Invoke(w, new object[] { curSecondName });

                                Find.WindowStack.TryRemove(w, true);
                                return ToolResult.Success(useSecond
                                    ? $"已命名: {curName} / {curSecondName}"
                                    : $"已命名: {curName}");
                            }
                            else if (optIdx == 1)
                            {
                                var nameGen = GiveNameNameGenField?.GetValue(w) as Func<string>;
                                if (nameGen == null) return ToolResult.Error("无法获取名称生成器");
                                var newName = nameGen();
                                GiveNameCurNameField?.SetValue(w, newName);
                                return ToolResult.Success($"已随机名称: {newName}");
                            }
                            else if (optIdx == 2 && useSecond)
                            {
                                var secondNameGen = GiveNameSecondNameGenField?.GetValue(w) as Func<string>;
                                if (secondNameGen == null) return ToolResult.Error("无法获取定居点名称生成器");
                                var newSecondName = secondNameGen();
                                GiveNameCurSecondNameField?.SetValue(w, newSecondName);
                                return ToolResult.Success($"已随机定居点名称: {newSecondName}");
                            }

                            return ToolResult.Error($"无效选项: {optIdx}");
                        }

                        if (curIdx == dialogIdx)
                            return ToolResult.Error($"弹框 [{dialogIdx}] 类型为 {w.GetType().Name}，暂不支持程序化选择");
                        curIdx++;
                    }

                    return ToolResult.Error($"找不到弹框 [{dialogIdx}]，可能已关闭或编号错误");
                }
                catch (Exception ex)
                {
                    return ToolResult.Error($"选择弹框选项失败: {ex.Message}");
                }
            });
        }
        public (int x, int y)? GetTargetPos(JsonElement? args) => null;
    }
}
