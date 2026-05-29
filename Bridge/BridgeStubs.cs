using System.Collections.Generic;
using System.Threading.Tasks;
using Verse;

namespace RimWorldMCP
{
    /// <summary>CCClient stub — Agent 已迁至 RimworkAgent，MCP 保留最小接口。</summary>
    public static class CCClient
    {
        public static bool IsConnected => false;
        public static bool IsReady => false;
        public static bool IsSendingMessage => false;
        public static Task SendEvent(string eventName, object payload) => Task.CompletedTask;
        public static Task SendEventText(string eventName, string category, string text, object? colonyStats = null) => Task.CompletedTask;
        public static Task SendAbort() => Task.CompletedTask;
    }

    /// <summary>GameContextProvider stub</summary>
    public static class GameContextProvider
    {
        public static string BuildGameContext() => "";
        public static string BuildColonyOverview(Map map, System.Collections.Generic.List<Pawn> colonists, int colonistCount) => "";
    }

    /// <summary>BridgeLifecycle stub — Agent 已接管 CCB 管理，MCP 保留最小接口。</summary>
    public static class BridgeLifecycle
    {
        public static bool DangerPaused => false;
        public static string DangerSummary => "";
        public static int PendingLevel12Count => 0;
        public static void ResetPendingLevel12Count() { }
        public static void Stop() { }
        public static void Tick() { }
        public static Task StartAsync(string sessionId) => Task.CompletedTask;
        public static string? FindCompanionDir() => null;
        public static string BuildMcpJson(int port) => "{}";
        public static bool IsCompanionInstalled() => false;
        public static bool IsInstalling => false;
        public static string InstallStatus => "";
        public static Task<bool> InstallCompanionAsync() => Task.FromResult(false);
        public static void InstallCompanion() { }
        public static void UninstallCompanion() { }
    }
}
