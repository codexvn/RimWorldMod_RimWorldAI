using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using RimWorldAgent.IPC;
using RimWorldAgent.IPC.Generated;

namespace RimWorldAgent.Core.AgentTransport
{
    internal sealed class AcpBackendProbeResult
    {
        public bool Success { get; }
        public string Message { get; }
        public List<SessionConfigOptionDto> ConfigOptions { get; }

        public AcpBackendProbeResult(bool success, string message, List<SessionConfigOptionDto>? configOptions = null)
        {
            Success = success;
            Message = message;
            ConfigOptions = configOptions ?? new List<SessionConfigOptionDto>();
        }
    }

    internal static class AcpBackendProbe
    {
        public static async Task<AcpBackendProbeResult> RunAsync(
            string nodePath,
            string hostEntryPoint,
            string projectPath,
            AcpAgentServerDefinition backend,
            int agentMcpPort,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            if (!File.Exists(hostEntryPoint))
                return Failure("Node ACP Host 文件不存在。");

            var host = new NodeAgentHost(
                nodePath,
                hostEntryPoint,
                Path.GetDirectoryName(hostEntryPoint) ?? AppDomain.CurrentDomain.BaseDirectory,
                projectPath,
                timeout,
                _ => { },
                _ => { });

            string? sessionId = null;
            try
            {
                host.Start();
                var initResponse = await host.SendAsync(
                    IpcMessageTypes.Initialize,
                    new InitializeRequest
                    {
                        HostVersion = typeof(AcpBackendProbe).Assembly.GetName().Version?.ToString() ?? "0.0.0",
                        Config = new AgentRuntimeConfig
                        {
                            Cwd = projectPath,
                            Backend = new BackendLaunch
                            {
                                Name = backend.Name,
                                Command = backend.Command,
                                Args = new List<string>(backend.Args),
                                WorkingDirectory = backend.WorkingDirectory ?? projectPath,
                                Environment = new Dictionary<string, string>(backend.Env)
                            },
                            Prompt = new PromptConfig(),
                            AgentMcpUrl = $"http://localhost:{agentMcpPort}/mcp"
                        }
                    },
                    cancellationToken);

                if (initResponse.Type == IpcMessageTypes.Error)
                {
                    var error = IpcJson.DeserializePayload<ErrorResponse>(initResponse);
                    return Failure(error.Message);
                }

                var initialized = IpcJson.DeserializePayload<InitializeResponse>(initResponse);
                var version = string.IsNullOrWhiteSpace(initialized.AgentVersion)
                    ? "未知版本"
                    : initialized.AgentVersion;

                var sessionResponse = await host.SendAsync(
                    IpcMessageTypes.NewSession,
                    new object(),
                    cancellationToken);
                if (sessionResponse.Type == IpcMessageTypes.Error)
                {
                    var error = IpcJson.DeserializePayload<ErrorResponse>(sessionResponse);
                    return Failure($"ACP 已启动，但拉取 Session Config 失败：{error.Message}");
                }

                var session = IpcJson.DeserializePayload<SessionResponse>(sessionResponse);
                sessionId = session.SessionId;
                var options = session.ConfigOptions ?? new List<SessionConfigOptionDto>();
                var optionCount = options.Count;
                return new AcpBackendProbeResult(
                    true,
                    $"ACP 启动成功：{initialized.AgentName} {version} · configOptions={optionCount}",
                    options);
            }
            catch (OperationCanceledException)
            {
                return Failure("ACP 启动测试已取消。");
            }
            catch (Exception ex)
            {
                return Failure($"ACP 启动测试失败：{FormatExceptionChain(ex)}");
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(sessionId))
                {
                    try
                    {
                        await host.SendAsync(
                            IpcMessageTypes.Close,
                            new SessionRequest { SessionId = sessionId! },
                            CancellationToken.None);
                    }
                    catch (Exception closeEx)
                    {
                        // dispose host below; close is best-effort for probe cleanup
                        System.Diagnostics.Debug.WriteLine(
                            $"[AcpBackendProbe] close session failed: {closeEx.GetType().Name}: {closeEx.Message}");
                    }
                }
                host.Dispose();
            }
        }

        private static AcpBackendProbeResult Failure(string message)
            => new AcpBackendProbeResult(false, message);

        private static string FormatExceptionChain(Exception ex)
        {
            var message = $"{ex.GetType().Name}: {ex.Message}";
            for (var inner = ex.InnerException; inner != null; inner = inner.InnerException)
                message += $" ← {inner.GetType().Name}: {inner.Message}";
            return message;
        }
    }
}
