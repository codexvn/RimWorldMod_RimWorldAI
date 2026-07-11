using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using RimWorldAgent.Core.AgentRuntime;

namespace RimWorldAgent
{
    /// <summary>定位并验证 Node.js 运行时，不管理任何 ACP backend 依赖。</summary>
    public static class NodeRuntimeLocator
    {
        public static string? Resolve(string? configuredPath)
        {
            if (!string.IsNullOrWhiteSpace(configuredPath))
                return TryFindNode(configuredPath);
            return FindNodeExecutable();
        }

        public static bool IsVersionSupported(string nodePath, int minimumMajor, out string version)
        {
            if (!TryGetVersion(nodePath, out version)) return false;
            var normalized = version.TrimStart('v', 'V');
            var separator = normalized.IndexOf('.');
            var majorText = separator < 0 ? normalized : normalized.Substring(0, separator);
            return int.TryParse(majorText, out var major) && major >= minimumMajor;
        }

        private static string? FindNodeExecutable()
        {
            var candidates = new List<string>();
            AddCandidate(candidates, TryFindNode("node"));
            AddCandidate(candidates, TryFindNode("node.exe"));

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                AddCandidate(candidates, Path.Combine(programFiles, "nodejs", "node.exe"));
                AddCandidate(candidates, Path.Combine(programFilesX86, "nodejs", "node.exe"));

                var nvmPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "nvm");
                if (Directory.Exists(nvmPath))
                {
                    try
                    {
                        foreach (var directory in Directory.GetDirectories(nvmPath).OrderByDescending(GetNodeVersionFromDirectory))
                            AddCandidate(candidates, Path.Combine(directory, "node.exe"));
                    }
                    catch (Exception ex)
                    {
                        CoreLog.Info($"[NodeRuntimeLocator] nvm 目录扫描失败: {FormatExceptionChain(ex)}");
                    }
                }

                AddCandidate(candidates, TryFindWithWhere("node"));
            }

            return candidates
                .Select(TryFindNode)
                .Where(path => path != null)
                .Select(path => path!)
                .Select(path => new { Path = path, Version = GetInstalledVersion(path) })
                .Where(item => item.Version != null)
                .OrderByDescending(item => item.Version)
                .Select(item => item.Path)
                .FirstOrDefault();
        }

        private static bool TryGetVersion(string nodePath, out string version)
        {
            version = "";
            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo(nodePath, "--version")
                    {
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };
                if (!process.Start()) return false;
                version = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit(3000);
                return process.ExitCode == 0 && !string.IsNullOrWhiteSpace(version);
            }
            catch (Exception ex)
            {
                CoreLog.Info($"[NodeRuntimeLocator] Node 版本读取失败 ({nodePath}): {FormatExceptionChain(ex)}");
                return false;
            }
        }

        private static Version? GetInstalledVersion(string nodePath)
        {
            if (!TryGetVersion(nodePath, out var value)) return null;
            var normalized = value.TrimStart('v', 'V');
            return Version.TryParse(normalized, out var version) ? version : null;
        }

        private static string? TryFindNode(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate)) return null;
            var resolvedCandidate = Environment.ExpandEnvironmentVariables(candidate!.Trim().Trim('"'));
            if (string.IsNullOrWhiteSpace(resolvedCandidate)) return null;
            var isBareNodeName = string.Equals(resolvedCandidate, "node", StringComparison.OrdinalIgnoreCase)
                || string.Equals(resolvedCandidate, "node.exe", StringComparison.OrdinalIgnoreCase);
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && isBareNodeName)
            {
                resolvedCandidate = FindOnPath(resolvedCandidate)
                    ?? TryFindWithWhere(resolvedCandidate)
                    ?? resolvedCandidate;
            }
            else if (isBareNodeName)
            {
                resolvedCandidate = FindOnPath(resolvedCandidate) ?? resolvedCandidate;
            }
            if (isBareNodeName && !Path.IsPathRooted(resolvedCandidate) && !File.Exists(resolvedCandidate))
                return null;
            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo(resolvedCandidate, "--version")
                    {
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };
                if (!process.Start()) return null;
                process.WaitForExit(3000);
                if (process.ExitCode != 0) return null;
                try
                {
                    var modulePath = process.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(modulePath)
                        && (Path.IsPathRooted(modulePath) || File.Exists(modulePath)))
                        return Path.GetFullPath(modulePath);
                }
                catch (Exception ex)
                {
                    CoreLog.Info($"[NodeRuntimeLocator] Node 路径解析失败 ({resolvedCandidate}): {FormatExceptionChain(ex)}");
                    return isBareNodeName ? null : resolvedCandidate;
                }
                return isBareNodeName ? null : resolvedCandidate;
            }
            catch (Exception ex)
            {
                CoreLog.Info($"[NodeRuntimeLocator] Node 探测失败 ({resolvedCandidate}): {FormatExceptionChain(ex)}");
                return null;
            }
        }

        private static string? TryFindWithWhere(string name)
        {
            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo("cmd.exe", "/d /c where " + name)
                    {
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };
                if (!process.Start()) return null;
                var output = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit(5000);
                if (process.ExitCode != 0) return null;
                return output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            }
            catch (Exception ex)
            {
                CoreLog.Info($"[NodeRuntimeLocator] PATH 探测失败: {FormatExceptionChain(ex)}");
                return null;
            }
        }

        private static string? FindOnPath(string executableName)
        {
            var path = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrWhiteSpace(path)) return null;

            foreach (var directory in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(directory)) continue;
                var candidate = Path.Combine(directory.Trim().Trim('"'), executableName);
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                    && !executableName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    var windowsCandidate = candidate + ".exe";
                    if (File.Exists(windowsCandidate)) return Path.GetFullPath(windowsCandidate);
                }
            }

            return null;
        }

        private static Version GetNodeVersionFromDirectory(string directory)
        {
            var name = Path.GetFileName(directory)?.TrimStart('v', 'V') ?? "";
            return Version.TryParse(name, out var version) ? version : new Version(0, 0);
        }

        private static void AddCandidate(List<string> candidates, string? candidate)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && !candidates.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                candidates.Add(candidate!);
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
