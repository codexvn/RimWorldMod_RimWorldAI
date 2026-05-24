using System.Linq;
using UnityEngine;
using Verse;

namespace RimWorldMCP
{
    public class Dialog_BridgeSettings : Window
    {
        private McpModSettings _settings;
        private string _inputText = "";
        private string _log = "";

        public Dialog_BridgeSettings(McpModSettings settings)
        {
            _settings = settings;
            doCloseButton = true;
            absorbInputAroundWindow = true;
            closeOnClickedOutside = false;
            draggable = true;
            resizeable = false;
        }

        public override Vector2 InitialSize => new Vector2(500f, 400f);

        public override void DoWindowContents(Rect inRect)
        {
            var listing = new Listing_Standard();
            listing.Begin(inRect);

            // 状态
            var state = McpClient.State switch
            {
                ClientState.Disconnected => "未连接",
                ClientState.Connecting => "建立连接...",
                ClientState.Handshake => "握手认证...",
                ClientState.Ready => "已就绪",
                _ => "未知"
            };
            listing.Label($"状态: {state}");

            listing.Gap(6f);

            // 消息日志
            listing.Label("消息:");
            var logRect = listing.GetRect(160f);
            Widgets.DrawBox(logRect);
            var lines = _log.Split('\n');
            var shown = lines.Length > 15 ? lines.Skip(lines.Length - 15) : lines;
            var y = 2f;
            foreach (var line in shown)
            {
                var h = Text.CalcHeight(line, logRect.width - 10);
                Widgets.Label(new Rect(5, y, logRect.width - 10, h), line);
                y += h + 1;
                if (y > 155) break;
            }

            listing.Gap(6f);

            // 输入
            listing.Label("发送:");
            _inputText = listing.TextEntry(_inputText);
            if (listing.ButtonText("发送") && !string.IsNullOrWhiteSpace(_inputText))
            {
                _ = McpClient.SendMessage(_inputText);
                _log += $"\n→ {_inputText}";
                _inputText = "";
            }

            listing.End();

            // 从 Incoming 队列拉取消息
            while (McpClient.Incoming.TryDequeue(out var msg))
                _log += $"\n← {msg}";
        }
    }
}
