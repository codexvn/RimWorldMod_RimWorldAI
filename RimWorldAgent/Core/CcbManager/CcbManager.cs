using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using RimWorldAgent.Core.AgentRuntime;

namespace RimWorldAgent.Core.CcbManager
{
    /// <summary>CCB (Node.js cc-companion) 子进程管理器。</summary>
    public class CcbManager : IDisposable
    {
        private Process? _process;
        private string _companionDir;
        private string _sessionsDir;
        private string _nodeExe;
        private int _ccbPort;
        private string? _ccbToken;
        private bool _ready;

        public bool IsReady => _ready;

        public CcbManager(string companionDir, string sessionsDir, int ccbPort = 19999, string? nodeExe = null, string? ccbToken = null)
        {
            _companionDir = companionDir;
            _sessionsDir = sessionsDir;
            _ccbPort = ccbPort;
            _ccbToken = ccbToken;
            _nodeExe = nodeExe ?? FindNodeExe();
        }

        public bool Start()
        {
            if (string.IsNullOrEmpty(_nodeExe) || !Directory.Exists(_companionDir))
            {
                CoreLog.Error($"[CcbManager] node={_nodeExe ?? "(null)"}, dir={_companionDir}");
                return false;
            }

            Directory.CreateDirectory(_sessionsDir);

            // 写入 MCP 配置（游戏 + Agent 内部 Tool）
            var mcpJson = "{\"mcpServers\":{"
                + "\"rimworld\":{\"type\":\"http\",\"url\":\"http://localhost:9877/mcp\"},"
                + "\"agent\":{\"type\":\"http\",\"url\":\"http://localhost:9878/mcp\"}"
                + "}}";
            System.IO.File.WriteAllText(System.IO.Path.Combine(_sessionsDir, ".mcp.json"), mcpJson);

            var args = $"--import tsx/esm companion/companion.ts"
                + $" --idle-timeout 30000"
                + $" --project-path \"{_sessionsDir}\"";

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = _nodeExe,
                    Arguments = args,
                    WorkingDirectory = _companionDir,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                psi.Environment["CCB_HOST"] = "0.0.0.0";
                psi.Environment["CCB_PORT"] = _ccbPort.ToString();
                if (!string.IsNullOrEmpty(_ccbToken))
                    psi.Environment["CCB_AUTH_TOKEN"] = _ccbToken;

                _process = Process.Start(psi);
                if (_process == null) { CoreLog.Error("[CcbManager] 无法启动进程"); return false; }

                _process.EnableRaisingEvents = true;
                _process.Exited += (_, _) => { _ready = false; CoreLog.Error($"[CcbManager] 进程退出 (code={_process.ExitCode})"); };
                _process.OutputDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        if (e.Data.Contains("就绪")) _ready = true;
                        CoreLog.Info($"[ccb] {e.Data}");
                    }
                };
                _process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) CoreLog.Error($"[ccb] {e.Data}"); };
                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();

                CoreLog.Info($"[CcbManager] 已启动 (PID={_process.Id}, port={_ccbPort})");
                return true;
            }
            catch (Exception ex) { CoreLog.Error($"[CcbManager] 启动异常: {ex.Message}"); return false; }
        }

        /// <summary>等待 CCB 就绪（最长 waitMs ms）</summary>
        public async Task<bool> WaitReadyAsync(int waitMs = 15000)
        {
            var deadline = Environment.TickCount + waitMs;
            while (Environment.TickCount < deadline)
            {
                if (_ready) return true;
                if (_process != null && _process.HasExited) return false;
                await Task.Delay(500);
            }
            return _ready;
        }

        public void Stop()
        {
            if (_process == null) return;
            try
            {
                if (!_process.HasExited) { _process.Kill(); _process.WaitForExit(3000); }
            }
            catch { }
            finally { _process.Dispose(); _process = null; _ready = false; }
        }

        public void Dispose() => Stop();

        // ---- 查找 node.exe ----
        private static string? FindNodeExe()
        {
            var names = Environment.OSVersion.Platform == PlatformID.Win32NT
                ? new[] { "node.exe" } : new[] { "node", "nodejs" };

            foreach (var name in names)
            {
                try
                {
                    var psi = new ProcessStartInfo("where", name)
                    { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
                    using var p = Process.Start(psi);
                    if (p == null) continue;
                    var output = p.StandardOutput.ReadToEnd().Trim();
                    p.WaitForExit(1000);
                    if (p.ExitCode == 0 && !string.IsNullOrEmpty(output))
                    {
                        var path = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)[0].Trim();
                        if (File.Exists(path)) return path;
                    }
                }
                catch { }
            }
            var defaultPath = Environment.OSVersion.Platform == PlatformID.Win32NT
                ? @"C:\Program Files\nodejs\node.exe" : "/usr/local/bin/node";
            return File.Exists(defaultPath) ? defaultPath : null;
        }
    }
}
