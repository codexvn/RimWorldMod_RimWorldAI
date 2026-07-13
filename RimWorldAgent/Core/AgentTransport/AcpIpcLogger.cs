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
        private static long _sequence;

        public static string? LogFilePath
        {
            get => _logFilePath;
            set
            {
                var enabled = false;
                lock (_lock)
                {
                    _logFilePath = value;
                    _enabled = !string.IsNullOrWhiteSpace(value);
                    if (_enabled) _sequence = 0;
                    enabled = _enabled;
                }
                if (enabled) WriteRaw("[lifecycle] IPC logging enabled");
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
            WriteRaw("[stderr] " + line);
        }

        /// <summary>记录 Node ACP 方法追踪</summary>
        public static void LogTrace(string message)
        {
            WriteRaw("[trace] " + message);
        }

        private static void WriteEntry(string direction, string type, string? requestId, string rawJson)
        {
            if (!IsEnabled) return;
            var id = string.IsNullOrWhiteSpace(requestId) ? "-" : requestId;
            WriteRaw($"{direction} type={type} requestId={id} {rawJson}");
        }

        private static void WriteRaw(string line)
        {
            Exception? failure = null;
            string? path = null;
            try
            {
                lock (_lock)
                {
                    path = _logFilePath;
                    if (!_enabled || string.IsNullOrWhiteSpace(path)) return;
                    var dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);
                    var sequence = ++_sequence;
                    File.AppendAllText(path, $"[{DateTime.UtcNow:O}] seq={sequence} {line}\n", Encoding.UTF8);
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            if (failure != null)
            {
                RimWorldAgent.Core.AgentRuntime.CoreLog.Warn(
                    $"[AcpIpcLogger] 写入 IPC 日志失败 path={path ?? "<null>"}: {FormatExceptionChain(failure)}");
            }
        }

        private static string FormatExceptionChain(Exception ex)
        {
            var message = $"{ex.GetType().Name}: {ex.Message}";
            for (var inner = ex.InnerException; inner != null; inner = inner.InnerException)
                message += $" ← {inner.GetType().Name}: {inner.Message}";
            return message;
        }
    }
}
