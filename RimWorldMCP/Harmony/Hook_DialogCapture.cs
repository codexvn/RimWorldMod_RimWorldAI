using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using HarmonyLib;
using RimWorldMCP.MapRendering;
using UnityEngine;
using Verse;

namespace RimWorldMCP.Harmony
{
    /// <summary>
    /// 弹框 IMGUI 捕获补丁。
    /// 拦截 Widgets.Label / ButtonText / BeginGroup / EndGroup，
    /// 在 OnGUI 期间记录控件坐标和文本，帧末构建 ASCII 输出。
    /// </summary>
    [StaticConstructorOnStartup]
    public static class Hook_DialogCapture
    {
        // ========== 状态 ==========

        private static TaskCompletionSource<string>? _pendingCapture;
        private static DialogCaptureBuffer? _currentBuffer;
        private static bool _captureActive;

        // Group 嵌套偏移栈（BeginGroup 累加，EndGroup 弹出）
        private static readonly Stack<Vector2> _groupOffsetStack = new();

        static Hook_DialogCapture()
        {
            var harmony = new HarmonyLib.Harmony("com.rimworldmcp.dialogcapture");

            // ① 所有窗口渲染完毕后触发 TCS
            harmony.Patch(
                AccessTools.Method(typeof(WindowStack), "WindowStackOnGUI"),
                postfix: new HarmonyMethod(typeof(Hook_DialogCapture), nameof(WindowStack_OnGUIPostfix))
            );

            // ② 跟踪 Group 嵌套（坐标偏移）
            harmony.Patch(
                AccessTools.Method(typeof(Widgets), nameof(Widgets.BeginGroup)),
                prefix: new HarmonyMethod(typeof(Hook_DialogCapture), nameof(BeginGroup_Prefix))
            );
            harmony.Patch(
                AccessTools.Method(typeof(Widgets), nameof(Widgets.EndGroup)),
                postfix: new HarmonyMethod(typeof(Hook_DialogCapture), nameof(EndGroup_Postfix))
            );

            // ③ 记录 Label
            harmony.Patch(
                AccessTools.Method(typeof(Widgets), "Label", new Type[] { typeof(Rect), typeof(string) }),
                prefix: new HarmonyMethod(typeof(Hook_DialogCapture), nameof(Label_Prefix))
            );
            harmony.Patch(
                AccessTools.Method(typeof(Widgets), "Label", new Type[] { typeof(Rect), typeof(TaggedString) }),
                prefix: new HarmonyMethod(typeof(Hook_DialogCapture), nameof(LabelTagged_Prefix))
            );

            // ④ 记录 ButtonText（挂载在内部方法 ButtonTextWorker，所有重载最终都调用它）
            harmony.Patch(
                AccessTools.Method(typeof(Widgets), "ButtonTextWorker"),
                prefix: new HarmonyMethod(typeof(Hook_DialogCapture), nameof(ButtonTextWorker_Prefix))
            );

            // ⑤ 记录窗口标题
            harmony.Patch(
                AccessTools.Method(typeof(Window), "InnerWindowOnGUI"),
                postfix: new HarmonyMethod(typeof(Hook_DialogCapture), nameof(InnerWindowOnGUI_Postfix))
            );

            McpLog.Info("[Hook_DialogCapture] Harmony 弹框捕获补丁已安装");
        }

        // ========== 公开 API ==========

        /// <summary>主线程设置捕获标志（通过 DispatchAsync 调用）</summary>
        public static void BeginCapture()
        {
            _pendingCapture = new TaskCompletionSource<string>();
            _captureActive = true;
            _currentBuffer = new DialogCaptureBuffer();
            _groupOffsetStack.Clear();
        }

        /// <summary>HTTP 线程等待下一帧 OnGUI 完成捕获</summary>
        public static async Task<string?> WaitForCaptureAsync(int timeoutMs = 5000)
        {
            var tcs = _pendingCapture;
            if (tcs == null) return "";

            var timeout = Task.Delay(timeoutMs);
            var completed = await Task.WhenAny(tcs.Task, timeout);
            if (completed == timeout)
            {
                _captureActive = false;
                _currentBuffer = null;
                _pendingCapture = null;
                return null; // 超时
            }
            return await tcs.Task;
        }

        // ========== Harmony 补丁 ==========

        /// <summary>所有窗口渲染完毕 → 构建 ASCII 并触发 TCS</summary>
        static void WindowStack_OnGUIPostfix()
        {
            if (!_captureActive || _pendingCapture == null) return;

            _captureActive = false;
            string result;
            try
            {
                result = _currentBuffer?.ToAsciiString() ?? "";
            }
            catch (Exception ex)
            {
                McpLog.Warn($"[Hook_DialogCapture] 构建 ASCII 失败: {ex.Message}");
                result = "";
            }

            _currentBuffer = null;
            _pendingCapture.TrySetResult(result);
            _pendingCapture = null;
        }

        // ---- Group 嵌套 ----

        static void BeginGroup_Prefix(Rect rect)
        {
            if (!_captureActive) return;
            if (_groupOffsetStack.Count > 0)
            {
                var prev = _groupOffsetStack.Peek();
                _groupOffsetStack.Push(new Vector2(prev.x + rect.x, prev.y + rect.y));
            }
            else
            {
                _groupOffsetStack.Push(new Vector2(rect.x, rect.y));
            }
        }

        static void EndGroup_Postfix()
        {
            if (!_captureActive) return;
            if (_groupOffsetStack.Count > 0)
                _groupOffsetStack.Pop();
        }

        // ---- Label ----

        static void Label_Prefix(Rect rect, string label)
        {
            if (!_captureActive || _currentBuffer == null) return;
            if (!IsCapturableWindow()) return;
            if (string.IsNullOrEmpty(label)) return;

            var abs = ToAbsoluteRect(rect);
            _currentBuffer.RecordLabel(label, abs.x, abs.y, abs.width);
        }

        static void LabelTagged_Prefix(Rect rect, TaggedString label)
        {
            if (!_captureActive || _currentBuffer == null) return;
            if (!IsCapturableWindow()) return;
            if (string.IsNullOrEmpty(label.RawText)) return;

            var abs = ToAbsoluteRect(rect);
            string text = label.Resolve(); // 翻译键 → 翻译文本
            _currentBuffer.RecordLabel(text, abs.x, abs.y, abs.width);
        }

        // ---- ButtonText ----

        static void ButtonTextWorker_Prefix(Rect rect, string label)
        {
            if (!_captureActive || _currentBuffer == null) return;
            if (!IsCapturableWindow()) return;
            if (string.IsNullOrEmpty(label)) return;

            var abs = ToAbsoluteRect(rect);
            _currentBuffer.RecordButton(label, abs.x, abs.y, abs.width);
        }

        // ---- 窗口标题 ----

        static void InnerWindowOnGUI_Postfix(Window __instance)
        {
            if (!_captureActive || _currentBuffer == null) return;
            if (!IsCapturableWindow()) return;

            // 窗口标题：optionalTitle 在 Header 位置绘制
            if (!__instance.optionalTitle.NullOrEmpty())
            {
                // RimWorld 标题位置：窗口 rect + Margin 偏移
                float titleX = __instance.windowRect.x;
                float titleY = __instance.windowRect.y;
                _currentBuffer.RecordWindowTitle(__instance.optionalTitle, titleX, titleY);
            }
        }

        // ========== 辅助 ==========

        /// <summary>过滤掉本 Mod 的 AI 聊天窗口</summary>
        private static bool IsCapturableWindow()
        {
            var win = Find.WindowStack?.currentlyDrawnWindow;
            if (win == null) return true; // 非窗口内的 label
            string name = win.GetType().FullName ?? win.GetType().Name;
            return !name.Contains("Dialog_AiChat") && !name.Contains("ImmediateWindow");
        }

        /// <summary>控件相对坐标 → 绝对屏幕坐标</summary>
        private static Rect ToAbsoluteRect(Rect controlRect)
        {
            var win = Find.WindowStack?.currentlyDrawnWindow;
            float winX = win?.windowRect.x ?? 0f;
            float winY = win?.windowRect.y ?? 0f;

            float groupX = 0f;
            float groupY = 0f;
            if (_groupOffsetStack.Count > 0)
            {
                var top = _groupOffsetStack.Peek();
                groupX = top.x;
                groupY = top.y;
            }

            return new Rect(
                winX + groupX + controlRect.x,
                winY + groupY + controlRect.y,
                controlRect.width,
                controlRect.height
            );
        }
    }
}
