using System;

namespace SimpleMspServer
{
    public static class SimpleLog
    {
        public static Action<string>? OnInfo;
        public static Action<string>? OnError;

        public static void Info(string msg) => OnInfo?.Invoke(msg);
        public static void Error(string msg) => OnError?.Invoke(msg);
    }
}
