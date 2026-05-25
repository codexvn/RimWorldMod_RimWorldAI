using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Verse;
using RimWorld;
using RimWorldMCP.Harmony;

namespace RimWorldMCP
{
    /// <summary>CC 桥接连接生命周期管理</summary>
    public static class BridgeLifecycle
    {
        private static int _nextCCEventTick;
        private const int CCEventCheckInterval = 120;
        private static Process? _companionProcess;

        public static async Task StartAsync()
        {
            var settings = RimWorldMCPMod.Instance?.Settings;
            if (settings == null || string.IsNullOrEmpty(settings.CCUrl)) return;

            if (settings.CCAutoStart)
            {
                McpLog.Info("[bridge] CCAutoStart=开启");
                McpLog.Info("[bridge] 步骤1: 停止当前进程...");
                StopCompanionProcess();
                McpLog.Info("[bridge] 步骤2: 清理残留 PID 文件...");
                KillStaleByPidFile();
                McpLog.Info("[bridge] 步骤3: 启动 Companion 进程...");
                StartCompanionProcess(settings.LocalCCPort, settings.CCToken);
                await Task.Delay(2000);
            }
            else
            {
                McpLog.Info("[bridge] CCAutoStart=关闭，仅连接远程 Companion");
            }

            await CCClient.Connect(settings.CCUrl, settings.CCToken);
            if (CCClient.IsReady)
            {
                McpLog.Info($"[bridge] 已连接到 CC Companion: {settings.CCUrl}");
            }
            else
            {
                McpLog.Error($"[bridge] CC Companion 连接失败: {settings.CCUrl}");
            }
        }

        public static void Tick()
        {
            CCClient.Tick();
            CCEventTick();
        }

        public static void Stop()
        {
            CCClient.Disconnect();
            StopCompanionProcess();
        }

        // ========== CC 事件转发 ==========

        private static void CCEventTick()
        {
            if (!CCClient.IsReady) return;

            if (NotificationBus.HighDangerPending)
            {
                NotificationBus.HighDangerPending = false;
                var emergencyList = NotificationBus.Drain();
                foreach (var n in emergencyList)
                {
                    if (NotificationBus.IsHighDanger(n.Type, n.DangerLabel, n.Priority))
                        SendCCEvent(n);
                }
            }

            var tick = Find.TickManager?.TicksGame ?? 0;
            if (tick < _nextCCEventTick) return;
            _nextCCEventTick = tick + CCEventCheckInterval;

            var notifications = NotificationBus.Drain();
            foreach (var n in notifications)
            {
                if (NotificationBus.IsHighDanger(n.Type, n.DangerLabel, n.Priority))
                    SendCCEvent(n);
            }
        }

        private static void SendCCEvent(Notification n)
        {
            string category;
            string text;

            switch (n.Type)
            {
                case NotificationType.Letter:
                    category = n.DangerLabel switch
                    {
                        "大威胁" => "RaidStart", "小威胁" => "RaidStart",
                        "死亡" => "PawnDeath", "负面" => "NegativeEvent",
                        _ => "AlertStart"
                    };
                    text = string.IsNullOrEmpty(n.Text) ? n.Label : $"{n.Label} — {n.Text}";
                    break;

                case NotificationType.Message:
                    category = n.DangerLabel switch
                    {
                        "大威胁" or "小威胁" => "RaidStart",
                        "死亡" => "PawnDeath", "负面" => "NegativeEvent",
                        _ => "AlertStart"
                    };
                    text = n.Text ?? n.Label;
                    break;

                case NotificationType.AlertStart:
                    category = "AlertStart";
                    text = n.Culprits != null && n.Culprits.Count > 0
                        ? $"{n.Label}: {string.Join(", ", n.Culprits.Take(5))}"
                        : n.Label;
                    break;

                default: return;
            }

            _ = CCClient.SendEventText("rimworld.alert", category, text);
        }

        // ========== Companion 目录/Node.js 查找 ==========

        private static string? FindModRoot()
        {
            try
            {
                var asmPath = typeof(BridgeLifecycle).Assembly.Location;
                if (string.IsNullOrEmpty(asmPath)) return null;
                var asmDir = Path.GetDirectoryName(asmPath);
                if (asmDir == null) return null;
                return Path.GetFullPath(Path.Combine(asmDir, "..", ".."));
            }
            catch { return null; }
        }

        private static string? FindNodeExe()
        {
            try
            {
                var psi = new ProcessStartInfo("node", "--version")
                { UseShellExecute = false, RedirectStandardOutput = true,
                  RedirectStandardError = true, CreateNoWindow = true };
                using var proc = Process.Start(psi);
                if (proc != null) { proc.WaitForExit(3000); if (proc.ExitCode == 0) return "node"; }
            }
            catch { }

            var candidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "nodejs", "node.exe"),
                Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "", "Programs", "nodejs", "node.exe"),
            };
            foreach (var c in candidates)
                if (File.Exists(c)) { McpLog.Info($"[cc] 找到 Node.js: {c}"); return c; }

            McpLog.Error("[cc] 找不到 Node.js。请安装 Node.js (https://nodejs.org)");
            return null;
        }

        private static string? FindCompanionDir()
        {
            var modRoot = FindModRoot();
            if (modRoot == null) return null;
            var dir = Path.Combine(modRoot, "cc-companion");
            if (Directory.Exists(dir) && File.Exists(Path.Combine(dir, "companion.ts")))
            { McpLog.Info($"[cc] Companion 目录: {dir}"); return dir; }
            McpLog.Error("[cc] 找不到 cc-companion 目录");
            return null;
        }

        // ========== 进程管理 ==========

        private static void KillStaleByPidFile()
        {
            var dir = FindCompanionDir();
            if (dir == null) return;
            var pidFile = Path.Combine(dir, ".pid");
            if (!File.Exists(pidFile)) { McpLog.Info("[cc] 无残留 PID 文件，跳过清理"); return; }

            try
            {
                var pidText = File.ReadAllText(pidFile).Trim();
                McpLog.Info($"[cc] 发现残留 PID 文件: {pidFile} (PID={pidText})");
                if (int.TryParse(pidText, out int pid))
                {
                    try
                    {
                        using var proc = Process.GetProcessById(pid);
                        McpLog.Info($"[cc] 正在杀死残留进程 PID={pid} ({proc.ProcessName})");
                        proc.Kill(); proc.WaitForExit(3000);
                        McpLog.Info($"[cc] 残留进程 PID={pid} 已终止");
                    }
                    catch (ArgumentException) { McpLog.Info($"[cc] PID={pid} 进程已不存在，仅清理 PID 文件"); }
                }
            }
            catch (Exception ex) { McpLog.Warn($"[cc] 读取/清理 PID 文件失败: {ex.Message}"); }
            finally { try { File.Delete(pidFile); } catch { } }
        }

        private static void StartCompanionProcess(int port, string token)
        {
            var nodeExe = FindNodeExe(); if (nodeExe == null) return;
            var companionDir = FindCompanionDir(); if (companionDir == null) return;

            var modRoot = FindModRoot();
            var sessionsDir = modRoot != null
                ? Path.Combine(modRoot, "claude-sessions")
                : Path.Combine(companionDir, "..", "claude-sessions");

            var settings = RimWorldMCPMod.Instance?.Settings;
            var mcpPort = settings?.McpPort ?? 9877;

            var args = $"--import tsx/esm companion.ts"
                + $" --port {port}"
                + $" --host 127.0.0.1"
                + $" --mcp-url http://localhost:{mcpPort}/mcp"
                + $" --project-path \"{sessionsDir}\"";
            if (!string.IsNullOrEmpty(token)) args += $" --token {token}";

            try
            {
                McpLog.Info($"[cc] 启动 Companion: {nodeExe} {args}");
                var psi = new ProcessStartInfo
                {
                    FileName = nodeExe, Arguments = args, WorkingDirectory = companionDir,
                    UseShellExecute = false, RedirectStandardOutput = true,
                    RedirectStandardError = true, CreateNoWindow = true,
                };

                _companionProcess = Process.Start(psi);
                if (_companionProcess == null) { McpLog.Error("[cc] 无法启动 Companion 进程"); return; }

                _companionProcess.EnableRaisingEvents = true;
                _companionProcess.Exited += (_, _) =>
                {
                    var exitCode = _companionProcess?.ExitCode;
                    McpLog.Warn($"[cc] Companion 进程已退出 (PID: {_companionProcess?.Id}, 退出码: {exitCode})");
                };
                _companionProcess.OutputDataReceived += (_, e) =>
                { if (!string.IsNullOrEmpty(e.Data)) McpLog.Info($"[js] {e.Data}"); };
                _companionProcess.ErrorDataReceived += (_, e) =>
                { if (!string.IsNullOrEmpty(e.Data)) McpLog.Warn($"[js] {e.Data}"); };
                _companionProcess.BeginOutputReadLine();
                _companionProcess.BeginErrorReadLine();

                McpLog.Info($"[cc] Companion 进程已启动 (PID: {_companionProcess.Id}, CWD: {companionDir})");
            }
            catch (Exception ex) { McpLog.Error($"[cc] 启动 Companion 进程失败: {ex.Message}"); }
        }

        private static void StopCompanionProcess()
        {
            if (_companionProcess == null) { McpLog.Info("[cc] 无需停止：无当前进程引用"); return; }
            try
            {
                if (!_companionProcess.HasExited)
                {
                    McpLog.Info($"[cc] 正在停止 Companion 进程 (PID: {_companionProcess.Id})...");
                    _companionProcess.Kill(); _companionProcess.WaitForExit(5000);
                    McpLog.Info("[cc] Companion 进程已停止");
                }
                else McpLog.Info($"[cc] Companion 进程已退出 (PID: {_companionProcess.Id})");
            }
            catch (Exception ex) { McpLog.Warn($"[cc] 停止 Companion 进程失败: {ex.Message}"); }
            finally { _companionProcess.Dispose(); _companionProcess = null; }
        }
    }
}
