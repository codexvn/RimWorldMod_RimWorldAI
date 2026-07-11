using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using RimWorldAgent.Core.AgentRuntime;

namespace RimWorldAgent.Core
{
    /// <summary>SQLitePCLRaw 原生 DLL 搜索路径配置</summary>
    public static class NativeResolver
    {
        private const int RtldNow = 2;
        private static bool _initialized;
        private static IntPtr _sqliteHandle;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetDllDirectoryW(string lpPathName);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibraryW(string lpFileName);

        [DllImport("libdl.so.2", EntryPoint = "dlopen", CharSet = CharSet.Ansi)]
        private static extern IntPtr DlopenLinux(string fileName, int flags);

        [DllImport("libdl.dylib", EntryPoint = "dlopen", CharSet = CharSet.Ansi)]
        private static extern IntPtr DlopenMac(string fileName, int flags);

        /// <summary>在首次 SQLite 调用前调用，将 Native\{rid}\ 追加到 DLL 搜索路径</summary>
        /// <remarks>
        /// Windows: SetDllDirectory 在进程启动目录后追加一条搜索路径，不影响系统目录 / PATH 等。
        /// Unix: 追加 LD_LIBRARY_PATH / DYLD_LIBRARY_PATH（动态链接器运行时读取）。
        /// </remarks>
        public static void Setup(string asmDir)
        {
            if (_initialized) return;

            var plat = Environment.OSVersion.Platform;
            var nativePath = FindNativeLibraryPath(asmDir, plat);

            if (nativePath == null)
            {
                CoreLog.Warn($"[NativeResolver] Native 目录缺失，基准目录: {asmDir}");
                return;
            }
            var nativeDir = Path.GetDirectoryName(nativePath) ?? "";

            try
            {
                if (plat == PlatformID.Win32NT)
                {
                    SetDllDirectoryW(nativeDir);
                    PrependPath("PATH", nativeDir, ";");

                    _sqliteHandle = LoadLibraryW(nativePath);
                    if (_sqliteHandle != IntPtr.Zero)
                    {
                        _initialized = true;
                        CoreLog.Info($"[NativeResolver] LoadLibrary → {nativePath}");
                    }
                    else
                    {
                        CoreLog.Warn($"[NativeResolver] LoadLibrary 失败: {nativePath} (err={Marshal.GetLastWin32Error()})");
                    }
                }
                else
                {
                    var key = plat == PlatformID.MacOSX ? "DYLD_LIBRARY_PATH" : "LD_LIBRARY_PATH";
                    PrependPath(key, nativeDir, ":");

                    _sqliteHandle = LoadUnixLibrary(nativePath);
                    if (_sqliteHandle != IntPtr.Zero)
                    {
                        _initialized = true;
                        CoreLog.Info($"[NativeResolver] dlopen → {nativePath}");
                    }
                    else
                    {
                        CoreLog.Warn($"[NativeResolver] dlopen 失败: {nativePath}");
                    }
                }
            }
            catch (Exception ex)
            {
                CoreLog.Warn($"[NativeResolver] 配置失败: {FormatExceptionChain(ex)}");
            }
        }

        private static void PrependPath(string key, string nativeDir, string separator)
        {
            var existing = Environment.GetEnvironmentVariable(key) ?? "";
            if (existing.IndexOf(nativeDir, StringComparison.OrdinalIgnoreCase) >= 0)
                return;

            Environment.SetEnvironmentVariable(key,
                string.IsNullOrEmpty(existing) ? nativeDir : nativeDir + separator + existing);
        }

        private static IntPtr LoadUnixLibrary(string nativePath)
        {
            if (nativePath.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase))
                return DlopenMac(nativePath, RtldNow);
            return DlopenLinux(nativePath, RtldNow);
        }

        private static string? FindNativeLibraryPath(string asmDir, PlatformID plat)
        {
            foreach (var baseDir in GetCandidateBaseDirs(asmDir))
            {
                foreach (var rid in GetRidCandidates(plat))
                {
                    var nativePath = Path.GetFullPath(Path.Combine(baseDir, AgentRuntimePaths.NativeDirectoryName, rid, GetNativeFileName(rid)));
                    if (File.Exists(nativePath))
                        return nativePath;
                }
            }

            return null;
        }

        private static IEnumerable<string> GetCandidateBaseDirs(string asmDir)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var dir in WalkParents(asmDir))
            {
                if (seen.Add(dir))
                    yield return dir;
            }

            foreach (var dir in WalkParents(AppDomain.CurrentDomain.BaseDirectory))
            {
                if (seen.Add(dir))
                    yield return dir;
            }
        }

        private static IEnumerable<string> WalkParents(string startDir)
        {
            if (string.IsNullOrWhiteSpace(startDir))
                yield break;

            var dir = Path.GetFullPath(startDir);
            while (!string.IsNullOrEmpty(dir))
            {
                yield return dir;
                var parent = Directory.GetParent(dir);
                if (parent == null) yield break;
                dir = parent.FullName;
            }
        }

        private static IEnumerable<string> GetRidCandidates(PlatformID plat)
        {
            if (plat == PlatformID.Win32NT)
            {
                yield return IntPtr.Size == 8 ? "win-x64" : "win-x86";
                yield break;
            }

            if (plat == PlatformID.MacOSX)
            {
                yield return "osx-x64";
                yield break;
            }

            yield return "linux-x64";
            yield return "osx-x64";
        }

        private static string GetNativeFileName(string rid)
            => rid.StartsWith("win-", StringComparison.OrdinalIgnoreCase)
                ? "e_sqlite3.dll"
                : rid.StartsWith("osx-", StringComparison.OrdinalIgnoreCase) ? "libe_sqlite3.dylib" : "libe_sqlite3.so";

        private static string FormatExceptionChain(Exception ex)
        {
            var message = $"{ex.GetType().Name}: {ex.Message}";
            for (var inner = ex.InnerException; inner != null; inner = inner.InnerException)
                message += $" <- {inner.GetType().Name}: {inner.Message}";
            return message;
        }
    }
}
