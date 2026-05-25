using UnityEngine;
using Verse;

namespace RimWorldMCP
{
    /// <summary>右下角 AI 对话开关按钮，对齐原版开关风格</summary>
    public class MapComponent_McpUI : MapComponent
    {
        public MapComponent_McpUI(Map map) : base(map) { }

        public override void MapComponentOnGUI()
        {
            base.MapComponentOnGUI();
            if (Find.CurrentMap == null) return;

            // 放在时间控件上方，不跟 PlaySettings WidgetRow 冲突
            float btnSize = 32f;
            float x = UI.screenWidth - 170f;
            float y = UI.screenHeight - 75f;
            Rect btnRect = new Rect(x, y, btnSize, btnSize);

            bool isOpen = Find.WindowStack.IsOpen<Dialog_AiChat>();
            bool streaming = ChatDisplayState.Snapshot.Count > 0
                && ChatDisplayState.Snapshot[ChatDisplayState.Snapshot.Count - 1].State == ChatState.Streaming;

            Color origColor = GUI.color;
            if (streaming)
                GUI.color = Time.realtimeSinceStartup % 1.0f < 0.5f ? Color.cyan : Color.white;
            else if (!GatewayClient.IsConnected)
                GUI.color = Color.grey;
            else if (isOpen)
                GUI.color = Color.cyan;

            if (Widgets.ButtonImage(btnRect, TexButton.Info))
            {
                if (isOpen)
                    Find.WindowStack.WindowOfType<Dialog_AiChat>()?.Close();
                else
                    Find.WindowStack.Add(new Dialog_AiChat());
            }
            GUI.color = origColor;

            // 开启状态下画小开关指示
            if (isOpen)
            {
                Rect markerRect = new Rect(btnRect.xMax - 8f, btnRect.yMax - 8f, 6f, 6f);
                Widgets.DrawBoxSolid(markerRect, Color.green);
            }
        }
    }
}
