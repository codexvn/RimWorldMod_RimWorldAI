using System;

namespace RimWorldAgent.Core.AgentRuntime
{
    /// <summary>Core 库内部日志出口。宿主（EXE/MOD）可注入。</summary>
    public static class CoreLog
    {
        public static Action<string>? OnInfo;
        public static Action<string>? OnWarn;
        public static Action<string>? OnError;

        public static void Info(string msg) => OnInfo?.Invoke(msg);
        public static void Warn(string msg) => OnWarn?.Invoke(msg);
        public static void Error(string msg) => OnError?.Invoke(msg);
    }
}
