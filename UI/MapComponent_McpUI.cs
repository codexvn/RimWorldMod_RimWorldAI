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

            // 右下角，在 RimWorld 底部栏和播放设置按钮上方
            float x = UI.screenWidth - 100f;
            float y = UI.screenHeight - 95f;
            Rect btnRect = new Rect(x, y, 92f, 30f);

            bool isOpen = Find.WindowStack.IsOpen<Dialog_AiChat>();
            Color origColor = GUI.color;
            if (!GatewayClient.IsConnected)
                GUI.color = Color.grey;
            else if (isOpen)
                GUI.color = Color.cyan;

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

            // 连接状态绿点
            if (GatewayClient.IsConnected)
            {
                Rect dotRect = new Rect(x - 8f, y + 12f, 6f, 6f);
                Widgets.DrawBoxSolid(dotRect, Color.green);
            }

            // 流式回复中闪烁指示
            var entries = ChatDisplayState.Snapshot;
            if (entries.Count > 0 && entries[entries.Count - 1].State == ChatState.Streaming)
            {
                Rect dotRect = new Rect(x - 18f, y + 12f, 6f, 6f);
                bool blink = Time.realtimeSinceStartup % 1.0f < 0.5f;
                if (blink)
                    Widgets.DrawBoxSolid(dotRect, Color.cyan);
            }
        }
    }
}
