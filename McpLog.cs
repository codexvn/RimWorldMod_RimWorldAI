using System;

namespace RimWorldMCP
{
    public static class McpLog
    {
        private static bool _useVerseLog = true;

        public static void Info(string msg)
        {
            Write("INFO", msg);
        }

        public static void Warn(string msg)
        {
            Write("WARN", msg);
        }

        public static void Error(string msg)
        {
            Write("ERROR", msg);
        }

        private static void Write(string level, string msg)
        {
            var line = $"[RimWorldMCP] {DateTime.Now:HH:mm:ss} [{level}] {msg}";

            try
            {
                if (_useVerseLog)
                {
                    switch (level)
                    {
                        case "WARN": Verse.Log.Warning(line); break;
                        case "ERROR": Verse.Log.Error(line); break;
                        default: Verse.Log.Message(line); break;
                    }
                }
                else
                {
                    Console.Error.WriteLine(line);
                }
            }
            catch
            {
                _useVerseLog = false;
                Console.Error.WriteLine(line);
            }
        }
    }
}
