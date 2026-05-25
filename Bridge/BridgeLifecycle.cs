using System;
using System.IO;
using System.Threading.Tasks;
using Verse;

namespace RimWorldMCP
{
    /// <summary>OpenClaw Gateway 连接生命周期管理 — 独立于 MCP Server</summary>
    public static class BridgeLifecycle
    {
        public static async Task StartAsync()
        {
            var settings = RimWorldMCPMod.Instance?.Settings;
            if (settings == null || settings.BridgeType == 0 || string.IsNullOrEmpty(settings.BridgeUrl))
                return;

            await GatewayClient.Connect(settings.BridgeUrl, settings.BridgeToken, settings.BridgePassword);
            if (GatewayClient.IsReady)
            {
                McpLog.Info($"[bridge] 已连接到 {McpModSettings.BridgeTypeLabels[settings.BridgeType]}: {settings.BridgeUrl}");
                SendSessionPrompt();
            }
        }

        public static void Tick()
        {
            GatewayMessageQueue.Tick();
            GatewayEventMonitor.Tick();
        }

        public static void Stop()
        {
            GatewayMessageQueue.Reset();
            GatewayClient.Disconnect();
        }

        private static void SendSessionPrompt()
        {
            var prompt = LoadPromptFile();
            if (string.IsNullOrEmpty(prompt)) return;

            _ = McpCommandQueue.DispatchAsync<bool>(() =>
            {
                GatewayMessageQueue.MarkSessionPromptSent();
                McpLog.Info("[bridge] 会话 Prompt 已入队");
                GatewayMessageQueue.Enqueue(MessageCategory.SessionInit, prompt);
                return true;
            });
        }

        private static string LoadPromptFile()
        {
            try
            {
                var path = FindPromptPath();
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    McpLog.Error($"[bridge] Prompt 文件不存在: {path}");
                    return "";
                }
                return File.ReadAllText(path, System.Text.Encoding.UTF8);
            }
            catch (Exception ex)
            {
                McpLog.Warn($"[bridge] 读取 Prompt 失败: {ex.Message}");
                return "";
            }
        }

        private static string FindPromptPath()
        {
            try
            {
                var asmPath = typeof(BridgeLifecycle).Assembly.Location;
                McpLog.Info($"[bridge] Assembly 路径: {asmPath ?? "null"}");
                if (string.IsNullOrEmpty(asmPath)) return "";

                var asmDir = Path.GetDirectoryName(asmPath);
                McpLog.Info($"[bridge] Assembly 目录: {asmDir ?? "null"}");
                if (asmDir == null) return "";

                // Assemblies/ → ../.. = mod root
                var modRoot = Path.GetFullPath(Path.Combine(asmDir, "..", ".."));
                var prompt = Path.Combine(modRoot, "Prompt.md");
                McpLog.Info($"[bridge] 尝试读取 Prompt: {prompt} (Exists={File.Exists(prompt)})");
                if (File.Exists(prompt)) return prompt;
            }
            catch (Exception ex)
            {
                McpLog.Debug($"[bridge] FindPromptPath 异常: {ex.Message}");
            }

            return "";
        }
    }
}
