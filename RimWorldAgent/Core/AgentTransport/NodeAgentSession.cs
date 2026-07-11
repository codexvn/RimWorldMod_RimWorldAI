using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RimWorldAgent.Core.AgentRuntime;
using RimWorldAgent.IPC;
using RimWorldAgent.IPC.Generated;

namespace RimWorldAgent.Core.AgentTransport
{
    internal sealed class NodeAgentSession : IAgentSession
    {
        private readonly AgentEngineConfig _cfg;
        private readonly AcpAgentLaunch _backend;
        private readonly NodeAgentHost _host;
        private readonly Action<string> _logInfo;
        private readonly Action<string> _logWarn;
        private readonly Action<string> _logError;
        private readonly NodeRuntimeEventProjector _projector;
        private bool _initialized;
        private bool _disposed;

        public string? SessionId { get; private set; }
        public bool IsReady => !_disposed && _initialized && _host.IsRunning && !string.IsNullOrEmpty(SessionId);
        public bool CanLoadSession { get; private set; }
        public bool CanResumeSession { get; private set; }
        private List<SessionConfigOptionDto> _lastConfigOptions = new List<SessionConfigOptionDto>();
        public IReadOnlyList<SessionConfigOptionDto> LastConfigOptions => _lastConfigOptions;

        public event Action? OnActivity;
        public event Action<string, string?>? OnResult;
        public event Func<string, string, string, Task>? OnToolUse;
        public event Action? OnAborted;

        public NodeAgentSession(AgentEngineConfig cfg, AcpAgentLaunch backend, string hostEntryPoint,
            Action<string> logInfo, Action<string> logWarn, Action<string> logError)
        {
            _cfg = cfg;
            _backend = backend;
            _logInfo = logInfo;
            _logWarn = logWarn;
            _logError = logError;
            _host = new NodeAgentHost(cfg.AcpNodePath, hostEntryPoint,
                Path.GetDirectoryName(hostEntryPoint) ?? AppDomain.CurrentDomain.BaseDirectory,
                cfg.ProjectPath, TimeSpan.FromSeconds(cfg.IpcRequestTimeoutSeconds), logInfo, logError, cfg.LogAcpIpc);
            _projector = new NodeRuntimeEventProjector(_backend.Name, logWarn,
                () => OnActivity?.Invoke(),
                (subtype, stopReason) => OnResult?.Invoke(subtype, stopReason),
                RaiseToolUse,
                () => OnAborted?.Invoke());
            _host.MessageReceived += HandleMessage;
            _host.ProcessExited += HandleProcessExited;
        }

        public async Task InitializeAsync(CancellationToken cancellationToken)
        {
            if (_initialized) return;
            _host.Start();
            var response = await _host.SendAsync(IpcMessageTypes.Initialize,
                new InitializeRequest
                {
                    HostVersion = typeof(NodeAgentSession).Assembly.GetName().Version?.ToString() ?? "0.0.0",
                    Config = BuildRuntimeConfig()
                }, cancellationToken);
            EnsureSuccess(response);
            var init = IpcJsonCompat.DeserializePayload<InitializeResponse>(response);
            CanLoadSession = init.LoadSession;
            CanResumeSession = init.ResumeSession;
            _initialized = true;
            _logInfo($"[NodeACP] initialized agent={init.AgentName} version={init.AgentVersion ?? "unknown"} load={init.LoadSession} resume={init.ResumeSession}");
        }

        public async Task NewAsync(CancellationToken cancellationToken)
        {
            EnsureInitialized();
            var response = await _host.SendAsync(IpcMessageTypes.NewSession, new object(), cancellationToken);
            EnsureSuccess(response);
            var session = IpcJsonCompat.DeserializePayload<SessionResponse>(response);
            SetSessionId(session.SessionId);
            CaptureConfigOptions(session.ConfigOptions);
            await ApplySavedSessionConfigAsync(cancellationToken);
        }

        private async Task ApplySavedSessionConfigAsync(CancellationToken cancellationToken)
        {
            var selections = _backend.SessionConfigSelections;
            if (selections == null || selections.Count == 0)
            {
                _logInfo("[NodeACP] 无已保存 Session Config，跳过 set_config_option");
                return;
            }

            var catalog = _lastConfigOptions;
            foreach (var selection in selections)
            {
                if (selection == null || string.IsNullOrWhiteSpace(selection.ConfigId))
                    continue;
                try
                {
                    var option = AcpSessionConfig.FindOption(catalog, selection.ConfigId);
                    if (option == null)
                    {
                        _logWarn($"[NodeACP] 跳过 configId={selection.ConfigId}：当前 session 无此选项");
                        continue;
                    }
                    if (!AcpSessionConfig.IsSelectionApplicable(option, selection, out var reason))
                    {
                        _logWarn($"[NodeACP] 跳过 configId={selection.ConfigId}：{reason}");
                        continue;
                    }
                    var type = string.IsNullOrWhiteSpace(selection.Type) ? option.Type : selection.Type;
                    await SetConfigOptionAsync(selection.ConfigId, type, selection.Value, cancellationToken);
                    catalog = _lastConfigOptions;
                }
                catch (Exception ex)
                {
                    _logWarn($"[NodeACP] set_config_option 失败 configId={selection.ConfigId}: {FormatExceptionChain(ex)}");
                }
            }
        }

        public async Task ResumeAsync(string sessionId, CancellationToken cancellationToken)
        {
            EnsureInitialized();
            var response = await _host.SendAsync(IpcMessageTypes.ResumeSession,
                new SessionRequest { SessionId = sessionId }, cancellationToken);
            EnsureSuccess(response);
            var session = IpcJsonCompat.DeserializePayload<SessionResponse>(response);
            SetSessionId(session.SessionId);
            CaptureConfigOptions(session.ConfigOptions);
        }

        public async Task LoadAsync(string sessionId, CancellationToken cancellationToken)
        {
            EnsureInitialized();
            var response = await _host.SendAsync(IpcMessageTypes.LoadSession,
                new SessionRequest { SessionId = sessionId }, cancellationToken);
            EnsureSuccess(response);
            var session = IpcJsonCompat.DeserializePayload<SessionResponse>(response);
            SetSessionId(session.SessionId);
            CaptureConfigOptions(session.ConfigOptions);
        }

        public async Task SetConfigOptionAsync(string configId, string type, string value, CancellationToken cancellationToken)
        {
            EnsureInitialized();
            if (string.IsNullOrWhiteSpace(SessionId))
                throw new InvalidOperationException("Node ACP session is not ready.");
            var response = await _host.SendAsync(
                IpcMessageTypes.SetSessionConfigOption,
                new SetSessionConfigOptionRequest
                {
                    SessionId = SessionId!,
                    ConfigId = configId,
                    Type = string.IsNullOrWhiteSpace(type) ? null : type,
                    Value = AcpSessionConfig.ValueToJsonElement(type, value)
                },
                cancellationToken);
            EnsureSuccess(response);
            var result = IpcJsonCompat.DeserializePayload<SetSessionConfigOptionResponse>(response);
            CaptureConfigOptions(result.ConfigOptions);
            _logInfo($"[NodeACP] set_config_option configId={configId} value={value} options={_lastConfigOptions.Count}");
        }

        public async Task PromptAsync(string prompt, CancellationToken cancellationToken)
        {
            EnsureInitialized();
            if (string.IsNullOrWhiteSpace(SessionId))
                throw new InvalidOperationException("Node ACP session is not ready.");
            var started = DateTime.UtcNow;
            var response = await _host.SendAsync(IpcMessageTypes.Prompt,
                new PromptRequest { SessionId = SessionId!, Prompt = prompt }, cancellationToken);
            EnsureSuccess(response);
            var result = IpcJsonCompat.DeserializePayload<PromptResponse>(response);
            _projector.ProjectPromptResponse(result, (long)(DateTime.UtcNow - started).TotalMilliseconds);
        }

        public async Task CancelAsync(CancellationToken cancellationToken)
        {
            if (!_initialized || string.IsNullOrEmpty(SessionId)) return;
            var response = await _host.SendAsync(IpcMessageTypes.Cancel,
                new SessionRequest { SessionId = SessionId! }, cancellationToken);
            EnsureSuccess(response);
            _projector.ProjectCancelled();
        }

        public async Task ClearAsync(CancellationToken cancellationToken)
        {
            if (!_initialized) return;
            if (!string.IsNullOrEmpty(SessionId))
            {
                try
                {
                    var response = await _host.SendAsync(IpcMessageTypes.Close,
                        new SessionRequest { SessionId = SessionId! }, cancellationToken);
                    EnsureSuccess(response);
                }
                catch (Exception ex)
                {
                    _logWarn("[NodeACP] close session failed, creating a new session: " + FormatExceptionChain(ex));
                }
            }
            SessionId = null;
            AgentLoop.AgentSessionId = null;
            await NewAsync(cancellationToken);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _host.Dispose();
        }

        private void HandleMessage(IpcEnvelope envelope)
        {
            try
            {
                if (envelope.Type == IpcMessageTypes.Event)
                {
                    _projector.Project(IpcJsonCompat.DeserializePayload<AgentEvent>(envelope));
                    return;
                }
                if (envelope.Type == IpcMessageTypes.Error)
                {
                    var error = IpcJsonCompat.DeserializePayload<ErrorResponse>(envelope);
                    _logError("[NodeACP] runtime error: " + error.Code + ": " + error.Message);
                    UIMessageBus.PushUiMessage(UiMessage.Error(error.Message));
                    return;
                }
                _logWarn("[NodeACP] unexpected asynchronous IPC message: " + envelope.Type);
            }
            catch (Exception ex)
            {
                _logError("[NodeACP] message handling failed: " + FormatExceptionChain(ex));
            }
        }

        private void HandleProcessExited(Exception? error)
        {
            if (_disposed) return;
            _initialized = false;
            _logError(error == null ? "[NodeACP] host exited unexpectedly." : "[NodeACP] host exited: " + FormatExceptionChain(error));
            UIMessageBus.PushUiMessage(UiMessage.Error("Agent Host 已退出，当前会话不可用。"));
            OnAborted?.Invoke();
        }

        private AgentRuntimeConfig BuildRuntimeConfig()
        {
            var skillsDescPath = string.IsNullOrWhiteSpace(_cfg.SkillsDescPath)
                ? Path.Combine(_cfg.ProjectPath, "skills-desc.txt")
                : _cfg.SkillsDescPath!;
            var systemPrompt = AgentSystemPromptLoader.Load(_cfg.PromptPath, _cfg.ProjectPath, skillsDescPath);
            var config = new AgentRuntimeConfig
            {
                Cwd = _cfg.ProjectPath,
                Backend = new BackendLaunch
                {
                    Name = _backend.Name,
                    Command = _backend.Command,
                    Args = _backend.Args.ToList(),
                    WorkingDirectory = _backend.WorkingDirectory,
                    Environment = _backend.Env.ToDictionary(pair => pair.Key, pair => pair.Value)
                },
                Prompt = new PromptConfig
                {
                    SystemPrompt = systemPrompt
                },
                AgentMcpUrl = $"http://localhost:{_cfg.AgentMcpPort}/mcp"
            };
            return config;
        }

        private void EnsureInitialized()
        {
            if (!_initialized || !_host.IsRunning)
                throw new InvalidOperationException("Node ACP Host is not initialized.");
        }

        private void EnsureSuccess(IpcEnvelope response)
        {
            if (response.Type != IpcMessageTypes.Error) return;
            var error = IpcJsonCompat.DeserializePayload<ErrorResponse>(response);
            throw new InvalidOperationException(error.Code + ": " + error.Message);
        }

        private void SetSessionId(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
                throw new InvalidDataException("Node ACP Host returned an empty session ID.");
            SessionId = sessionId;
            AgentLoop.AgentSessionId = sessionId;
            AgentLoop.RaiseSessionIdChanged(sessionId);
            _logInfo("[NodeACP] sessionId received: " + sessionId);
        }

        private void CaptureConfigOptions(List<SessionConfigOptionDto>? options)
        {
            _lastConfigOptions = options == null
                ? new List<SessionConfigOptionDto>()
                : new List<SessionConfigOptionDto>(options);
        }

        private void RaiseToolUse(string id, string name, string input)
        {
            var handlers = OnToolUse;
            if (handlers == null) return;
            foreach (Func<string, string, string, Task> handler in handlers.GetInvocationList())
                _ = InvokeToolHandler(handler, id, name, input);
        }

        private async Task InvokeToolHandler(Func<string, string, string, Task> handler, string id, string name, string input)
        {
            try { await handler(id, name, input); }
            catch (Exception ex) { _logError("[NodeACP] ToolUse handler failed: " + FormatExceptionChain(ex)); }
        }

        private static string FormatExceptionChain(Exception ex)
        {
            var message = $"{ex.GetType().Name}: {ex.Message}";
            for (var inner = ex.InnerException; inner != null; inner = inner.InnerException)
                message += $" ← {inner.GetType().Name}: {inner.Message}";
            return message;
        }
    }

    internal static class IpcJsonCompat
    {
        public static T DeserializePayload<T>(IpcEnvelope envelope)
            => envelope.Payload.HasValue
                ? JsonSerializer.Deserialize<T>(envelope.Payload.Value.GetRawText(), IpcJson.Options) ?? throw new InvalidDataException("IPC payload is null.")
                : throw new InvalidDataException("IPC payload is missing.");
    }
}
