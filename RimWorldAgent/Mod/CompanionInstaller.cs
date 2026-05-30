using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using RimWorldAgent.Core.AgentRuntime;

namespace RimWorldAgent
{
    /// <summary>cc-companion npm 安装/卸载管理 + Node.js/npm/compaion 目录查找（从旧 BridgeLifecycle 迁移）</summary>
    public static class CompanionInstaller
    {
        private static volatile bool _installing;
        private static volatile string _status = "";

        public static bool IsInstalling => _installing;
        public static string InstallStatus => _status;

        // ========== 公开 API ==========

        public static bool IsInstalled(string? companionDir)
        {
            if (string.IsNullOrEmpty(companionDir) || !Directory.Exists(companionDir)) return false;
            return Directory.Exists(Path.Combine(companionDir!, "node_modules"));
        }

        /// <summary>异步安装（CCB 启动前 await，确保 node_modules 就绪）</summary>
        public static Task<bool> InstallAsync(string companionDir) => DoInstallAsync(companionDir);

        /// <summary>同步安装（设置 UI 调用，fire-and-forget）</summary>
        public static void Install(string companionDir)
        {
            if (_installing) return;
            _ = DoInstallAsync(companionDir);
        }

        private static async Task<bool> DoInstallAsync(string companionDir)
        {
            if (string.IsNullOrEmpty(companionDir) || !Directory.Exists(companionDir))
            { _status = "找不到 cc-companion 目录"; return false; }
            if (_installing) return false;

            _installing = true;
            _status = "正在安装...";

            var success = await Task.Run(() =>
            {
                try
                {
                    var npmPath = FindNpmPath();
                    if (npmPath == null)
                    { _status = "找不到 npm，请确保已安装 Node.js (https://nodejs.org)"; return false; }

                    ProcessStartInfo psi;
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                        && !npmPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        psi = new ProcessStartInfo("cmd", $"/c \"{npmPath}\" install --no-audit --no-fund --loglevel=error")
                        {
                            WorkingDirectory = companionDir, UseShellExecute = false,
                            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
                        };
                    }
                    else
                    {
                        psi = new ProcessStartInfo(npmPath, "install --no-audit --no-fund --loglevel=error")
                        {
                            WorkingDirectory = companionDir, UseShellExecute = false,
                            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
                        };
                    }

                    using var proc = Process.Start(psi);
                    if (proc == null) { _status = "无法启动 npm install"; return false; }
                    proc.OutputDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) _status = e.Data; };
                    proc.ErrorDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) _status = e.Data; };
                    proc.BeginOutputReadLine();
                    proc.BeginErrorReadLine();
                    proc.WaitForExit(300000);
                    if (proc.ExitCode == 0) { _status = "安装完成"; return true; }
                    else { _status = $"npm install 失败，退出码: {proc.ExitCode}"; return false; }
                }
                catch (Exception ex) { _status = $"安装失败: {ex.Message}"; return false; }
                finally { _installing = false; }
            });

            return success;
        }

        public static void Uninstall(string companionDir)
        {
            if (string.IsNullOrEmpty(companionDir)) return;
            var nodeModules = Path.Combine(companionDir, "node_modules");
            if (!Directory.Exists(nodeModules)) return;
            try { Directory.Delete(nodeModules, true); _status = "已卸载"; }
            catch (Exception ex) { _status = $"卸载失败: {ex.Message}"; }
        }

        // ========== Node.js 查找 ==========

        public static string? FindNodeExe()
        {
            // 1. bare "node" via PATH
            var node = TryFindNode("node");
            if (node != null) return node;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // 2. "node.exe" explicitly
                node = TryFindNode("node.exe");
                if (node != null) return node;

                // 3. Common install paths
                var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                var pfx86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                var commonPaths = new[] { Path.Combine(pf, "nodejs", "node.exe"), Path.Combine(pfx86, "nodejs", "node.exe") };

                // 4. nvm
                var nvmPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "nvm");
                if (Directory.Exists(nvmPath))
                {
                    try
                    {
                        var versions = Directory.GetDirectories(nvmPath)
                            .Select(d => Path.Combine(d, "node.exe"))
                            .Where(File.Exists).OrderByDescending(v => v).ToArray();
                        foreach (var v in versions)
                        { node = TryFindNode(v); if (node != null) return node; }
                    }
                    catch (Exception ex) { CoreLog.Info($"[CompanionInstaller] nvm 目录扫描失败: {ex.Message}"); }
                }

                // 5. Common paths
                foreach (var p in commonPaths)
                { if (File.Exists(p)) { node = TryFindNode(p); if (node != null) return node; } }
            }

            // 6. cmd /c where (Windows shell PATH, most reliable)
            var whereNode = TryFindWithWhere("node");
            if (whereNode != null) return whereNode;

            return null;
        }

        public static string? FindNpmPath()
        {
            // 1. 基于 node 路径推导同目录 npm
            var nodeExe = FindNodeExe();
            var needsUpdate = false;
            if (nodeExe != null)
            {
                var nodeDir = Path.GetDirectoryName(nodeExe);
                if (!string.IsNullOrEmpty(nodeDir))
                {
                    var npmName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "npm.cmd" : "npm";
                    var candidate = Path.Combine(nodeDir, npmName);
                    if (File.Exists(candidate)) return candidate;
                }
            }

            // 2. cmd /c where npm
            var whereNpm = TryFindWithWhere("npm");
            if (whereNpm != null) return whereNpm;

            // 3. PATH 兜底
            try
            {
                var psi = new ProcessStartInfo("npm", "--version")
                { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                using var proc = Process.Start(psi);
                if (proc != null) { proc.WaitForExit(3000); if (proc.ExitCode == 0) return "npm"; }
            }
            catch (Exception ex) { CoreLog.Info($"[CompanionInstaller] npm 探测失败: {ex.Message}"); }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try
                {
                    var psi = new ProcessStartInfo("npm.cmd", "--version")
                    { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                    using var proc = Process.Start(psi);
                    if (proc != null) { proc.WaitForExit(3000); if (proc.ExitCode == 0) return "npm.cmd"; }
                }
                catch (Exception ex) { CoreLog.Info($"[CompanionInstaller] npm.cmd 探测失败: {ex.Message}"); }
            }

            return null;
        }

        /// <summary>查找 companion 目录：优先 publish 打包版本，回退源码目录</summary>
        public static string? FindCompanionDir(string? modRoot = null)
        {
            if (modRoot == null) modRoot = FindModRoot();
            if (modRoot == null) return null;

            // publish: ../../cc-companion (Mod in Mods/RimWorldAgent/About/../cc-companion)
            var pub = Path.GetFullPath(Path.Combine(modRoot, "..", "cc-companion"));
            if (Directory.Exists(pub) && File.Exists(Path.Combine(pub, "companion", "companion.ts")))
                return pub;

            // source: ../../../../cc-companion (Mod in RimWorldAgent/Mod/bin/Debug/...)
            var src = Path.GetFullPath(Path.Combine(modRoot, "..", "..", "..", "..", "cc-companion"));
            if (Directory.Exists(src) && File.Exists(Path.Combine(src, "companion", "companion.ts")))
                return src;

            return null;
        }

        // ========== 内部工具 ==========

        private static string? FindModRoot()
        {
            try
            {
                var rootDir = RimWorldAgentMod.Instance?.Content?.RootDir;
                if (!string.IsNullOrEmpty(rootDir)) return rootDir;
            }
            catch (Exception ex) { CoreLog.Info($"[CompanionInstaller] 读取 Mod RootDir 失败: {ex.Message}"); }

            try
            {
                var asmPath = typeof(CompanionInstaller).Assembly.Location;
                if (!string.IsNullOrEmpty(asmPath))
                {
                    var asmDir = Path.GetDirectoryName(asmPath);
                    if (asmDir != null) return Path.GetFullPath(Path.Combine(asmDir, "..", ".."));
                }
            }
            catch (Exception ex) { CoreLog.Info($"[CompanionInstaller] 读取 Assembly 路径失败: {ex.Message}"); }
            return null;
        }

        private static string? TryFindNode(string candidate)
        {
            try
            {
                var psi = new ProcessStartInfo(candidate, "--version")
                { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                using var proc = Process.Start(psi);
                if (proc != null) { proc.WaitForExit(3000); if (proc.ExitCode == 0) return candidate; }
            }
            catch (Exception ex) { CoreLog.Info($"[CompanionInstaller] Node 探测失败 ({candidate}): {ex.Message}"); }
            return null;
        }

        private static string? TryFindWithWhere(string name)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return null;
            try
            {
                var psi = new ProcessStartInfo("cmd", "/c where " + name)
                { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                using var proc = Process.Start(psi);
                if (proc == null) return null;
                proc.WaitForExit(5000);
                if (proc.ExitCode != 0) return null;
                var output = proc.StandardOutput.ReadToEnd().Trim();
                if (string.IsNullOrEmpty(output)) return null;
                var firstLine = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)[0].Trim();
                return File.Exists(firstLine) ? firstLine : null;
            }
            catch (Exception ex) { CoreLog.Info($"[CompanionInstaller] TryFindWithWhere 失败: {ex.Message}"); return null; }
        }
    }
}
