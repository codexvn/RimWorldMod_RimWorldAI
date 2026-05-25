using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace RimWorldMCP
{
    /// <summary>右下角 AI 对话开关按钮 + 工具调用状态文字</summary>
    public class MapComponent_McpUI : MapComponent
    {
        public MapComponent_McpUI(Map map) : base(map) { }

        public override void MapComponentOnGUI()
        {
            base.MapComponentOnGUI();
            if (Find.CurrentMap == null) return;

            // 放在时间控件上方，对齐原版 ToggleableIcon 大小
            float btnSize = 24f;
            float x = UI.screenWidth - 162f;
            float y = UI.screenHeight - 72f;
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

                if (isOpen)
                    SoundDefOf.Tick_Low.PlayOneShotOnCamera(null);
                else
                    SoundDefOf.Tick_High.PlayOneShotOnCamera(null);
            }
            GUI.color = origColor;

            if (isOpen)
            {
                Rect markerRect = new Rect(btnRect.xMax - 6f, btnRect.yMax - 6f, 4f, 4f);
                Widgets.DrawBoxSolid(markerRect, Color.green);
            }

            // 勾选框叠加层，匹配原版 ToggleableIcon 样式
            Rect cbRect = new Rect(btnRect.x + btnRect.width / 2f, btnRect.y,
                btnRect.height / 2f, btnRect.height / 2f);
            Texture2D cbTex = isOpen ? Widgets.CheckboxOnTex : Widgets.CheckboxOffTex;
            GUI.DrawTexture(cbRect, cbTex);
        }

    }
}
