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
    /// <summary>桥接连接生命周期管理 — 支持 OpenClaw Gateway 和 CC Companion</summary>
    public static class BridgeLifecycle
    {
        private static int _nextCCEventTick;
        private const int CCEventCheckInterval = 120;

        private static Process? _companionProcess;

        public static async Task StartAsync()
        {
            var settings = RimWorldMCPMod.Instance?.Settings;
            if (settings == null || settings.BridgeType == 0) return;

            if (settings.BridgeType == 1) // OpenClaw
            {
                if (string.IsNullOrEmpty(settings.BridgeUrl)) return;
                await GatewayClient.Connect(settings.BridgeUrl, settings.BridgeToken, settings.BridgePassword);
                if (GatewayClient.IsReady)
                {
                    McpLog.Info($"[bridge] 已连接到 OpenClaw: {settings.BridgeUrl}");
                    SendSessionPrompt();
                }
            }
            else if (settings.BridgeType == 2) // CC
            {
                if (string.IsNullOrEmpty(settings.CCUrl)) return;

                if (settings.CCAutoStart)
                {
                    StopCompanionProcess();
                    KillStaleByPidFile();
                    StartCompanionProcess(settings.LocalCCPort, settings.CCToken);
                    await Task.Delay(2000);
                }

                await CCClient.Connect(settings.CCUrl, settings.CCToken);
                if (CCClient.IsReady)
                {
                    McpLog.Info($"[bridge] 已连接到 CC Companion: {settings.CCUrl}");
                }
                else
                {
                    McpLog.Warn($"[bridge] CC Companion 连接失败: {settings.CCUrl}");
                }
            }
        }

        public static void Tick()
        {
            var settings = RimWorldMCPMod.Instance?.Settings;
            if (settings == null) return;

            if (settings.BridgeType == 1) // OpenClaw
            {
                GatewayMessageQueue.Tick();
                GatewayEventMonitor.Tick();
            }
            else if (settings.BridgeType == 2) // CC
            {
                CCClient.Tick();
                CCEventTick();
            }
        }

        public static void Stop()
        {
            GatewayMessageQueue.Reset();
            GatewayClient.Disconnect();
            CCClient.Disconnect();
            StopCompanionProcess();
        }

        // ========== CC 事件转发 ==========

        private static void CCEventTick()
        {
            if (!CCClient.IsReady) return;

            // 每帧：高危通知即时推送
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

            // 每 120 tick：普通通知
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
                        "大威胁" => "RaidStart",
                        "小威胁" => "RaidStart",
                        "死亡" => "PawnDeath",
                        "负面" => "NegativeEvent",
                        _ => "AlertStart"
                    };
                    text = string.IsNullOrEmpty(n.Text) ? n.Label : $"{n.Label} — {n.Text}";
                    break;

                case NotificationType.Message:
                    category = n.DangerLabel switch
                    {
                        "大威胁" or "小威胁" => "RaidStart",
                        "死亡" => "PawnDeath",
                        "负面" => "NegativeEvent",
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

                default:
                    return;
            }

            _ = CCClient.SendEventText("rimworld.alert", category, text);
        }

        private static void SendSessionPrompt()
        {
            var prompt = LoadPromptFile();
            if (string.IsNullOrEmpty(prompt)) return;

            _ = McpCommandQueue.DispatchAsync<bool>(() =>
            {
                GatewayMessageQueue.MarkSessionPromptSent();
                McpLog.Info("[bridge] 会话 Prompt 已入队");
                GatewayMessageQueue.Enqueue(MessageCategory.SessionInit, prompt);
                return true;
            });
        }

        private static string LoadPromptFile()
        {
            try
            {
                var path = FindPromptPath();
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    McpLog.Error($"[bridge] Prompt 文件不存在: {path}");
                    return "";
                }
                return File.ReadAllText(path, System.Text.Encoding.UTF8);
            }
            catch (Exception ex)
            {
                McpLog.Warn($"[bridge] 读取 Prompt 失败: {ex.Message}");
                return "";
            }
        }

        private static string FindPromptPath()
        {
            try
            {
                var asmPath = typeof(BridgeLifecycle).Assembly.Location;
                McpLog.Info($"[bridge] Assembly 路径: {asmPath ?? "null"}");
                if (string.IsNullOrEmpty(asmPath)) return "";

                var asmDir = Path.GetDirectoryName(asmPath);
                McpLog.Info($"[bridge] Assembly 目录: {asmDir ?? "null"}");
                if (asmDir == null) return "";

                // Assemblies/ → ../.. = mod root
                var modRoot = Path.GetFullPath(Path.Combine(asmDir, "..", ".."));
                var prompt = Path.Combine(modRoot, "Prompt.md");
                McpLog.Info($"[bridge] 尝试读取 Prompt: {prompt} (Exists={File.Exists(prompt)})");
                if (File.Exists(prompt)) return prompt;
            }
            catch (Exception ex)
            {
                McpLog.Debug($"[bridge] FindPromptPath 异常: {ex.Message}");
            }

            return "";
        }

        // ========== CC Companion 进程管理 ==========

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
            catch (Exception ex)
            {
                McpLog.Debug($"[cc] FindModRoot 异常: {ex.Message}");
                return null;
            }
        }

        private static string? FindNodeExe()
        {
            // 先尝试 PATH 中的 node
            try
            {
                var psi = new ProcessStartInfo("node", "--version")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    proc.WaitForExit(3000);
                    if (proc.ExitCode == 0)
                    {
                        McpLog.Info($"[cc] 找到 Node.js (PATH)");
                        return "node";
                    }
                }
            }
            catch { /* PATH 中无 node */ }

            // Windows 常见安装路径
            var candidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "nodejs", "node.exe"),
                Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "", "Programs", "nodejs", "node.exe"),
            };

            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    McpLog.Info($"[cc] 找到 Node.js: {candidate}");
                    return candidate;
                }
            }

            McpLog.Error("[cc] 找不到 Node.js。请安装 Node.js (https://nodejs.org)");
            return null;
        }

        private static string? FindCompanionDir()
        {
            var modRoot = FindModRoot();
            if (modRoot == null) return null;

            var dir = Path.Combine(modRoot, "cc-companion");
            if (Directory.Exists(dir) && File.Exists(Path.Combine(dir, "companion.ts")))
            {
                McpLog.Info($"[cc] Companion 目录: {dir}");
                return dir;
            }

            McpLog.Error("[cc] 找不到 cc-companion 目录");
            return null;
        }

        private static void KillStaleByPidFile()
        {
            var dir = FindCompanionDir();
            if (dir == null) return;
            var pidFile = Path.Combine(dir, ".pid");
            if (!File.Exists(pidFile)) return;

            try
            {
                var pidText = File.ReadAllText(pidFile).Trim();
                if (int.TryParse(pidText, out int pid))
                {
                    try
                    {
                        using var proc = Process.GetProcessById(pid);
                        McpLog.Info($"[cc] 清理残留进程 PID={pid}");
                        proc.Kill();
                        proc.WaitForExit(3000);
                    }
                    catch { /* 进程已不存在 */ }
                }
            }
            catch { }
            finally { try { File.Delete(pidFile); } catch { } }
        }

        private static void StartCompanionProcess(int port, string token)
        {
            var nodeExe = FindNodeExe();
            if (nodeExe == null) return;

            var companionDir = FindCompanionDir();
            if (companionDir == null) return;

            // 会话存在 Mod 目录下的 claude-sessions/
            var modRoot = FindModRoot();
            var sessionsDir = modRoot != null
                ? Path.Combine(modRoot, "claude-sessions")
                : Path.Combine(companionDir, "..", "claude-sessions");

            var args = $"--import tsx/esm companion.ts --port {port} --project-path \"{sessionsDir}\"";
            if (!string.IsNullOrEmpty(token))
                args += $" --token {token}";

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = nodeExe,
                    Arguments = args,
                    WorkingDirectory = companionDir,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };

                _companionProcess = Process.Start(psi);
                if (_companionProcess == null)
                {
                    McpLog.Error("[cc] 无法启动 Companion 进程");
                    return;
                }

                _companionProcess.OutputDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                        McpLog.Info($"[cc-companion] {e.Data}");
                };
                _companionProcess.ErrorDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                        McpLog.Warn($"[cc-companion] {e.Data}");
                };
                _companionProcess.BeginOutputReadLine();
                _companionProcess.BeginErrorReadLine();

                McpLog.Info($"[cc] Companion 进程已启动 (PID: {_companionProcess.Id})");
            }
            catch (Exception ex)
            {
                McpLog.Error($"[cc] 启动 Companion 进程失败: {ex.Message}");
            }
        }

        private static void StopCompanionProcess()
        {
            if (_companionProcess == null) return;

            try
            {
                if (!_companionProcess.HasExited)
                {
                    McpLog.Info($"[cc] 正在停止 Companion 进程 (PID: {_companionProcess.Id})...");
                    _companionProcess.Kill();
                    _companionProcess.WaitForExit(5000);
                    McpLog.Info("[cc] Companion 进程已停止");
                }
            }
            catch (Exception ex)
            {
                McpLog.Warn($"[cc] 停止 Companion 进程失败: {ex.Message}");
            }
            finally
            {
                _companionProcess.Dispose();
                _companionProcess = null;
            }
        }
    }
}
