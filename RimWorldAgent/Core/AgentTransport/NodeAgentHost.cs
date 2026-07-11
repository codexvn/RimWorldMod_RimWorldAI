using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RimWorldAgent.IPC;
using RimWorldAgent.IPC.Generated;

namespace RimWorldAgent.Core.AgentTransport
{
    internal sealed class NodeAgentHost : IDisposable
    {
        private const int MaxIpcLineBytes = 4 * 1024 * 1024;
        private readonly string _nodePath;
        private readonly string _hostEntryPoint;
        private readonly string _workingDirectory;
        private readonly string _projectPath;
        private readonly TimeSpan _requestTimeout;
        private readonly Action<string> _logInfo;
        private readonly Action<string> _logError;
        private readonly ConcurrentDictionary<string, TaskCompletionSource<IpcEnvelope>> _pending =
            new ConcurrentDictionary<string, TaskCompletionSource<IpcEnvelope>>();
        private readonly CancellationTokenSource _disposeCts = new CancellationTokenSource();
        private readonly object _writeLock = new object();
        private Process? _process;
        private StreamWriter? _stdin;
        private bool _disposed;

        public event Action<IpcEnvelope>? MessageReceived;
        public event Action<Exception?>? ProcessExited;

        public bool IsRunning => !_disposed && _process != null && !_process.HasExited;

        public NodeAgentHost(string nodePath, string hostEntryPoint, string workingDirectory, string projectPath,
            TimeSpan requestTimeout, Action<string> logInfo, Action<string> logError)
        {
            _nodePath = string.IsNullOrWhiteSpace(nodePath) ? "node" : nodePath;
            _hostEntryPoint = hostEntryPoint;
            _workingDirectory = workingDirectory;
            _projectPath = projectPath;
            _requestTimeout = requestTimeout > TimeSpan.Zero ? requestTimeout : TimeSpan.FromMinutes(5);
            _logInfo = logInfo;
            _logError = logError;
        }

        public void Start()
        {
            if (_process != null) return;
            if (!File.Exists(_hostEntryPoint))
                throw new FileNotFoundException("Node ACP Host entry point not found.", _hostEntryPoint);

            var startInfo = new ProcessStartInfo
            {
                FileName = _nodePath,
                WorkingDirectory = _workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.Arguments = QuoteArgument(_hostEntryPoint);
            startInfo.EnvironmentVariables["RIMWORLD_AGENT_PROJECT_PATH"] = _projectPath;

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.Exited += (_, __) => HandleExit(process);
            if (!process.Start())
                throw new InvalidOperationException("Failed to start Node ACP Host.");

            _process = process;
            _stdin = process.StandardInput;
            _ = Task.Run(ReadStdoutLoop);
            _ = Task.Run(ReadStderrLoop);
            _logInfo($"[NodeACP] host started pid={process.Id}");
        }

        public async Task<IpcEnvelope> SendAsync<T>(string type, T payload, CancellationToken cancellationToken)
        {
            if (!IsRunning || _stdin == null)
                throw new InvalidOperationException("Node ACP Host is not running.");

            var requestId = Guid.NewGuid().ToString("N");
            var pending = new TaskCompletionSource<IpcEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_pending.TryAdd(requestId, pending))
                throw new InvalidOperationException("Failed to register IPC request.");

            try
            {
                var envelope = IpcJson.Create(type, requestId, payload);
                var line = IpcJson.Serialize(envelope);
                EnsureMessageSize(line);
                lock (_writeLock)
                {
                    _stdin.WriteLine(line);
                    _stdin.Flush();
                }

                var cancellation = Task.Delay(Timeout.Infinite, cancellationToken);
                var timeout = Task.Delay(_requestTimeout);
                var completed = await Task.WhenAny(pending.Task, cancellation, timeout);
                if (completed == timeout)
                    throw new TimeoutException($"Node ACP Host request '{type}' timed out after {_requestTimeout.TotalSeconds:0} seconds.");
                if (completed == cancellation)
                    throw new OperationCanceledException(cancellationToken);
                return await pending.Task;
            }
            finally
            {
                _pending.TryRemove(requestId, out _);
            }
        }

        public void SendNotification<T>(string type, T payload)
        {
            if (!IsRunning || _stdin == null) return;
            var line = IpcJson.Serialize(IpcJson.Create(type, null, payload));
            EnsureMessageSize(line);
            lock (_writeLock)
            {
                _stdin.WriteLine(line);
                _stdin.Flush();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _disposeCts.Cancel();
            try { _stdin?.Close(); } catch (Exception ex) { _logError("[NodeACP] close stdin failed: " + FormatExceptionChain(ex)); }
            try
            {
                if (_process != null && !_process.HasExited)
                {
                    _process.Kill();
                    _process.WaitForExit(3000);
                }
            }
            catch (Exception ex) { _logError("[NodeACP] stop host failed: " + FormatExceptionChain(ex)); }
            foreach (var pair in _pending)
                pair.Value.TrySetException(new InvalidOperationException("Node ACP Host stopped."));
            _pending.Clear();
            _process?.Dispose();
            _process = null;
        }

        private async Task ReadStdoutLoop()
        {
            try
            {
                var process = _process;
                if (process == null) return;
                while (!_disposeCts.IsCancellationRequested)
                {
                    var line = await process.StandardOutput.ReadLineAsync();
                    if (line == null) break;
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (Encoding.UTF8.GetByteCount(line) > MaxIpcLineBytes)
                    {
                        _logError("[NodeACP] IPC message exceeds the 4 MiB limit.");
                        continue;
                    }
                    try
                    {
                        var envelope = IpcJson.Deserialize(line);
                        var requestId = envelope.RequestId;
                        if (requestId != null && requestId.Length > 0 &&
                            _pending.TryGetValue(requestId, out var waiter))
                        {
                            waiter.TrySetResult(envelope);
                        }
                        else
                        {
                            MessageReceived?.Invoke(envelope);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logError("[NodeACP] invalid IPC message: " + FormatExceptionChain(ex));
                    }
                }
            }
            catch (Exception ex)
            {
                if (!_disposeCts.IsCancellationRequested)
                    _logError("[NodeACP] stdout reader failed: " + FormatExceptionChain(ex));
            }
        }

        private async Task ReadStderrLoop()
        {
            try
            {
                var process = _process;
                if (process == null) return;
                while (!_disposeCts.IsCancellationRequested)
                {
                    var line = await process.StandardError.ReadLineAsync();
                    if (line == null) break;
                    if (!string.IsNullOrWhiteSpace(line)) _logInfo("[NodeACP] " + line);
                }
            }
            catch (Exception ex)
            {
                if (!_disposeCts.IsCancellationRequested)
                    _logError("[NodeACP] stderr reader failed: " + FormatExceptionChain(ex));
            }
        }

        private void HandleExit(Process process)
        {
            if (_disposed) return;
            var message = process.ExitCode == 0
                ? null
                : new InvalidOperationException("Node ACP Host exited with code " + process.ExitCode + ".");
            foreach (var pair in _pending)
                pair.Value.TrySetException(message ?? new InvalidOperationException("Node ACP Host exited."));
            ProcessExited?.Invoke(message);
        }

        private static string QuoteArgument(string value)
        {
            if (value.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return value;
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private static void EnsureMessageSize(string line)
        {
            if (Encoding.UTF8.GetByteCount(line) > MaxIpcLineBytes)
                throw new InvalidDataException("IPC message exceeds the 4 MiB limit.");
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
