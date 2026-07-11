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

        public AcpBackendProbeResult(bool success, string message)
        {
            Success = success;
            Message = message;
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

            try
            {
                host.Start();
                var response = await host.SendAsync(
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

                if (response.Type == IpcMessageTypes.Error)
                {
                    var error = IpcJson.DeserializePayload<ErrorResponse>(response);
                    return Failure(error.Message);
                }

                var initialized = IpcJson.DeserializePayload<InitializeResponse>(response);
                var version = string.IsNullOrWhiteSpace(initialized.AgentVersion)
                    ? "未知版本"
                    : initialized.AgentVersion;
                return new AcpBackendProbeResult(
                    true,
                    $"ACP 启动成功：{initialized.AgentName} {version}");
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
