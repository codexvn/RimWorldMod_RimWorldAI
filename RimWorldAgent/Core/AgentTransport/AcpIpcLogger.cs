using System;
using System.IO;
using System.Text;

namespace RimWorldAgent.Core.AgentTransport
{
    /// <summary>
    /// 写入完整 IPC NDJSON 数据流到独立日志文件，类似旧 CcbWebSocket.WsLogFilePath。
    /// 线程安全：所有写操作持有锁。
    /// </summary>
    internal static class AcpIpcLogger
    {
        private static readonly object _lock = new object();
        private static string? _logFilePath;
        private static bool _enabled;

        public static string? LogFilePath
        {
            get => _logFilePath;
            set
            {
                lock (_lock)
                {
                    _logFilePath = value;
                    _enabled = !string.IsNullOrWhiteSpace(value);
                }
            }
        }

        public static bool IsEnabled
        {
            get { lock (_lock) return _enabled; }
        }

        /// <summary>记录 C# → Node 的请求/通知</summary>
        public static void LogSend(string type, string? requestId, string rawJson)
        {
            WriteEntry("→", type, requestId, rawJson);
        }

        /// <summary>记录 Node → C# 的响应/事件</summary>
        public static void LogReceive(string type, string? requestId, string rawJson)
        {
            WriteEntry("←", type, requestId, rawJson);
        }

        /// <summary>记录 Node stderr 日志行</summary>
        public static void LogStderr(string line)
        {
            WriteRaw($"[{DateTime.UtcNow:O}] [stderr] {line}\n");
        }

        /// <summary>记录 Node ACP 方法追踪</summary>
        public static void LogTrace(string message)
        {
            WriteRaw($"[{DateTime.UtcNow:O}] [trace] {message}\n");
        }

        private static void WriteEntry(string direction, string type, string? requestId, string rawJson)
        {
            if (!IsEnabled) return;
            var id = string.IsNullOrWhiteSpace(requestId) ? "-" : requestId;
            WriteRaw($"[{DateTime.UtcNow:O}] {direction} type={type} requestId={id} {rawJson}\n");
        }

        private static void WriteRaw(string line)
        {
            var path = _logFilePath;
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                lock (_lock)
                {
                    var dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);
                    File.AppendAllText(path, line, Encoding.UTF8);
                }
            }
            catch
            {
                // 日志失败不能影响 IPC 主流程
            }
        }
    }
}
