namespace SimpleMspServer
{
    public interface IMspLog
    {
        void Debug(string message);
        void Info(string message);
        void Warn(string message);
        void Error(string message);
    }

    public sealed class NullMspLog : IMspLog
    {
        public static readonly NullMspLog Instance = new();
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message) { }
    }

    public sealed class DelegateMspLog : IMspLog
    {
        private readonly System.Action<string> _write;
        public DelegateMspLog(System.Action<string> write) => _write = write;
        public void Debug(string m) => _write($"[debug] {m}");
        public void Info(string m)  => _write($"[info] {m}");
        public void Warn(string m)  => _write($"[warn] {m}");
        public void Error(string m) => _write($"[error] {m}");
    }
}
