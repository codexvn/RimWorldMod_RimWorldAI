using UnityEngine;
using Verse;

namespace RimWorldMCP
{
    /// <summary>右下角工具栏 "AI 对话" 按钮</summary>
    public class MapComponent_McpUI : MapComponent
    {
        public MapComponent_McpUI(Map map) : base(map) { }

        public override void MapComponentOnGUI()
        {
            base.MapComponentOnGUI();
            if (Find.CurrentMap == null) return;

            float x = UI.screenWidth - 80f;
            float y = UI.screenHeight - 35f;
            Rect btnRect = new Rect(x, y, 72f, 30f);

            bool isOpen = Find.WindowStack.IsOpen<Dialog_AiChat>();
            Color origColor = GUI.color;
            GUI.color = isOpen ? Color.cyan : Color.white;
            if (Widgets.ButtonText(btnRect, "AI 对话"))
            {
                if (isOpen)
                {
                    var existing = Find.WindowStack.WindowOfType<Dialog_AiChat>();
                    existing?.Close();
                }
                else
                {
                    Find.WindowStack.Add(new Dialog_AiChat());
                }
            }
            GUI.color = origColor;

            // 连接状态小圆点
            if (GatewayClient.IsConnected)
            {
                Rect dotRect = new Rect(x + 74f, y + 11f, 6f, 6f);
                Widgets.DrawBoxSolid(dotRect, Color.green);
            }

            // 流式回复中闪烁指示
            var entries = ChatDisplayState.Snapshot;
            if (entries.Count > 0 && entries[entries.Count - 1].State == ChatState.Streaming)
            {
                Rect dotRect = new Rect(x + 74f, y + 2f, 6f, 6f);
                bool blink = Time.realtimeSinceStartup % 1.0f < 0.5f;
                GUI.color = blink ? Color.cyan : Color.clear;
                Widgets.DrawBoxSolid(dotRect, Color.cyan);
                GUI.color = origColor;
            }
        }
    }
}
