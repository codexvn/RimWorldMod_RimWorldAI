using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
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
        private static int _nextCCFallbackMs;
        private const int CCFallbackIntervalMs = 5000;
        private static Process? _companionProcess;
        private static string _currentSessionId = "";

        public static async Task StartAsync(string sessionId)
        {
            _currentSessionId = sessionId;
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
                StartCompanionProcess(settings.LocalCCPort, settings.CCToken, sessionId);
                await Task.Delay(2000);
            }
            else
            {
                McpLog.Info("[bridge] CCAutoStart=关闭，仅连接远程 Companion");
            }

            await CCClient.Connect(settings.CCUrl, settings.CCToken);
            if (CCClient.IsReady)
            {
                McpLog.Info($"[bridge] 已连接到 Claude Code: {settings.CCUrl}");
            }
            else
            {
                McpLog.Error($"[bridge] Claude Code 连接失败: {settings.CCUrl}");
            }
        }

        public static void Tick()
        {
            // Companion 进程健康监控——崩溃时自动重启
            if (_companionProcess != null && _companionProcess.HasExited)
            {
                McpLog.Error($"[cc] Companion 进程意外退出 (退出码: {_companionProcess.ExitCode})，正在重启...");
                StopCompanionProcess();
                KillStaleByPidFile();
                var settings = RimWorldMCPMod.Instance?.Settings;
                if (settings?.CCAutoStart == true)
                {
                    StartCompanionProcess(settings.LocalCCPort, settings.CCToken, _currentSessionId);
                    _ = CCClient.Connect(settings.CCUrl, settings.CCToken);
                }
            }

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

            // 高危事件：每帧立即推送（Harmony Patch 在暂停时仍会拦截，不受 TicksGame 影响）
            if (NotificationBus.HighDangerPending)
            {
                NotificationBus.HighDangerPending = false;
                var emergencyList = NotificationBus.Drain();
                SendCCEvents(emergencyList);
            }

            var tick = Find.TickManager?.TicksGame ?? 0;
            bool tickElapsed = tick >= _nextCCEventTick;
            if (tickElapsed)
                _nextCCEventTick = tick + CCEventCheckInterval;

            // 兜底定时器：暂停时 TicksGame 不前进，用 wall clock 保证定期推送
            int nowMs = Environment.TickCount;
            bool fallbackElapsed = unchecked((uint)(nowMs - _nextCCFallbackMs) >= CCFallbackIntervalMs);
            if (fallbackElapsed)
                _nextCCFallbackMs = nowMs;

            if (!tickElapsed && !fallbackElapsed) return;

            var notifications = NotificationBus.Drain();
            SendCCEvents(notifications);
        }

        private static string FormatGameEvent(string category, string rawText)
        {
            var icon = category switch
            {
                "RaidStart" => "⚠️ [紧急]",
                "RaidEnd" => "✅",
                "PawnDeath" => "💀 [紧急]",
                "NegativeEvent" => "⚠️",
                "AlertStart" => "⚠️",
                "DailyMorning" => "🌅",
                "IdleDetected" => "⏳",
                _ => "📢"
            };
            var instruction = category switch
            {
                "RaidStart" => "\n请立即评估威胁并指挥防御。",
                "PawnDeath" => "\n请检查殖民地状态并评估影响。",
                "DailyMorning" => "\n请做全面的殖民地检查。",
                "NegativeEvent" => "\n请评估严重程度并给出应对建议。",
                "AlertStart" => "\n请检查并处理此警报。",
                "IdleDetected" => "\n请检查是否有待分配的工作。",
                _ => ""
            };
            return $"{icon} {rawText}{instruction}";
        }

        private static void SendCCEvents(List<Notification> notifications)
        {
            if (notifications.Count == 0) return;

            var sb = new StringBuilder();
            string primaryCategory = "AlertStart";

            for (int i = 0; i < notifications.Count; i++)
            {
                var n = notifications[i];
                if (!TryFormatNotification(n, out var category, out var rawText)) continue;

                if (i == 0) primaryCategory = category;
                sb.AppendLine(FormatGameEvent(category, rawText));
            }

            var text = sb.ToString().TrimEnd();
            if (text.Length > 0)
                _ = CCClient.SendEventText("rimworld.alert", primaryCategory, text);
        }

        private static bool TryFormatNotification(Notification n, out string category, out string rawText)
        {
            switch (n.Type)
            {
                case NotificationType.Letter:
                    category = n.DangerLabel switch
                    {
                        "大威胁" => "RaidStart", "小威胁" => "RaidStart",
                        "死亡" => "PawnDeath", "负面" => "NegativeEvent",
                        _ => "AlertStart"
                    };
                    rawText = string.IsNullOrEmpty(n.Text) ? n.Label : $"{n.Label} — {n.Text}";
                    return true;

                case NotificationType.Message:
                    category = n.DangerLabel switch
                    {
                        "大威胁" or "小威胁" => "RaidStart",
                        "死亡" => "PawnDeath", "负面" => "NegativeEvent",
                        _ => "AlertStart"
                    };
                    rawText = n.Text ?? n.Label;
                    return true;

                case NotificationType.AlertStart:
                    category = "AlertStart";
                    rawText = n.Culprits != null && n.Culprits.Count > 0
                        ? $"{n.Label}: {string.Join(", ", n.Culprits.Take(5))}"
                        : n.Label;
                    return true;

                default:
                    category = rawText = "";
                    return false;
            }
        }

        // ========== 公开 API（设置 UI 调用） ==========

        public static bool IsNodeAvailable() => FindNodeExe() != null;

        public static bool IsCompanionInstalled()
        {
            var dir = FindCompanionDir();
            if (dir == null) return false;
            return Directory.Exists(Path.Combine(dir, "node_modules"));
        }

        public static bool IsInstalling { get; private set; }
        public static string InstallStatus { get; private set; } = "";

        public static void InstallCompanion()
        {
            var dir = FindCompanionDir();
            if (dir == null) { InstallStatus = "找不到 cc-companion 目录"; return; }
            if (IsInstalling) return;

            IsInstalling = true;
            InstallStatus = "正在安装...";
            McpLog.Info($"[cc] 开始安装 Companion 依赖...");
            _ = Task.Run(() =>
            {
                var log = new System.Text.StringBuilder();
                try
                {
                    var psi = new ProcessStartInfo("npm", "install")
                    {
                        WorkingDirectory = dir,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                    };
                    using var proc = Process.Start(psi);
                    if (proc == null) { InstallStatus = "无法启动 npm install"; IsInstalling = false; return; }
                    proc.OutputDataReceived += (_, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                        { log.AppendLine(e.Data); InstallStatus = e.Data; McpLog.Info($"[npm] {e.Data}"); }
                    };
                    proc.ErrorDataReceived += (_, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                        { log.AppendLine(e.Data); McpLog.Warn($"[npm] {e.Data}"); }
                    };
                    proc.BeginOutputReadLine();
                    proc.BeginErrorReadLine();
                    proc.WaitForExit(120000);
                    if (proc.ExitCode == 0)
                    { InstallStatus = "安装完成"; McpLog.Info("[cc] Companion 依赖安装完成"); }
                    else
                    { InstallStatus = $"npm install 失败，退出码: {proc.ExitCode}"; McpLog.Error($"[cc] {InstallStatus}"); }
                }
                catch (Exception ex)
                { InstallStatus = $"安装失败: {ex.Message}"; McpLog.Error($"[cc] {InstallStatus}"); }
                finally { IsInstalling = false; }
            });
        }

        public static void UninstallCompanion()
        {
            var dir = FindCompanionDir();
            if (dir == null) return;
            var nodeModules = Path.Combine(dir, "node_modules");
            if (!Directory.Exists(nodeModules)) { McpLog.Info("[cc] node_modules 不存在，无需卸载"); return; }

            McpLog.Info($"[cc] 删除 {nodeModules} ...");
            try { Directory.Delete(nodeModules, true); McpLog.Info("[cc] Companion 依赖已卸载"); }
            catch (Exception ex) { McpLog.Error($"[cc] 卸载失败: {ex.Message}"); }
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

        public static string? FindNodeExe()
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

            McpLog.Error("[cc] 未找到 Node.js，请确保已安装并加入 PATH (https://nodejs.org)");
            return null;
        }

        public static string? FindCompanionDir()
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

        private static void StartCompanionProcess(int port, string token, string sessionId)
        {
            var nodeExe = FindNodeExe(); if (nodeExe == null) return;
            var companionDir = FindCompanionDir(); if (companionDir == null) return;

            var modRoot = FindModRoot();
            var baseSessionsDir = modRoot != null
                ? Path.Combine(modRoot, "claude-sessions")
                : Path.Combine(companionDir, "..", "claude-sessions");
            var sessionsDir = Path.Combine(baseSessionsDir, $"rimworld-{sessionId}");
            Directory.CreateDirectory(sessionsDir);
            McpLog.Info($"[cc] 会话目录 (按存档): {sessionsDir}");

            var settings = RimWorldMCPMod.Instance?.Settings;
            var mcpPort = settings?.McpPort ?? 9877;
            var mcpConfig = $"{{\"rimworld\":{{\"type\":\"http\",\"url\":\"http://localhost:{mcpPort}/mcp\"}}}}";

            var args = $"--import tsx/esm companion.ts"
                + $" --port {port}"
                + $" --host 127.0.0.1"
                + $" --mcp-config \"{mcpConfig}\""
                + $" --project-path \"{sessionsDir}\"";
            if (!string.IsNullOrEmpty(token)) args += $" --token {token}";
            if (!string.IsNullOrEmpty(settings?.CCApiKey)) args += $" --api-key {settings!.CCApiKey}";
            if (!string.IsNullOrEmpty(settings?.CCApiBaseUrl)) args += $" --api-base-url {settings!.CCApiBaseUrl}";
            if (!string.IsNullOrEmpty(settings?.CCModelName)) args += $" --model-name {settings!.CCModelName}";

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
