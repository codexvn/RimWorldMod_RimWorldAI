using System.Collections.Generic;
using System.Reflection;
using Verse;
using RimWorld;

namespace RimWorldMCP.Helpers
{
    public static class DialogHelper
    {
        public static readonly FieldInfo FloatMenuOptionsField =
            typeof(FloatMenu).GetField("options", BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>
        /// 获取当前可交互的游戏弹框（排除本 mod 的 AI 聊天窗）。
        /// 顺序：FloatMenu 在前，Dialog 层在后，与 get_open_dialogs / select_dialog_option 索引一致。
        /// </summary>
        public static List<Window> GetInteractableDialogs()
        {
            var result = new List<Window>();
            var stack = Find.WindowStack;
            if (stack == null) return result;

            // FloatMenu 优先
            foreach (var w in stack.Windows)
                if (w is FloatMenu)
                    result.Add(w);

            // Dialog / SubSuper / Super 层，排除 FloatMenu 和本 mod 聊天窗
            // Dialog_NodeTreeWithFactionInfo 等可能在 SubSuper 层
            foreach (var w in stack.Windows)
                if ((w.layer == WindowLayer.Dialog || w.layer == WindowLayer.SubSuper || w.layer == WindowLayer.Super)
                    && w is not FloatMenu && w.GetType().Name != "Dialog_AiChat")
                    result.Add(w);

            return result;
        }
    }
}
