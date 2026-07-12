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
        public List<string> AppliedConfigIds { get; }

        public AcpBackendProbeResult(
            bool success,
            string message,
            List<SessionConfigOptionDto>? configOptions = null,
            List<string>? appliedConfigIds = null)
        {
            Success = success;
            Message = message;
            ConfigOptions = configOptions ?? new List<SessionConfigOptionDto>();
            AppliedConfigIds = appliedConfigIds ?? new List<string>();
        }
    }

    internal sealed class AcpBackendProbeConfigApplyResult
    {
        public List<SessionConfigOptionDto> ConfigOptions { get; }
        public int AppliedCount { get; }
        public int SkippedCount { get; }
        public List<string> AppliedConfigIds { get; }

        public AcpBackendProbeConfigApplyResult(
            List<SessionConfigOptionDto> configOptions,
            int appliedCount,
            int skippedCount,
            List<string> appliedConfigIds)
        {
            ConfigOptions = configOptions;
            AppliedCount = appliedCount;
            SkippedCount = skippedCount;
            AppliedConfigIds = appliedConfigIds;
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
                var applyResult = await ApplyProbeSelectionsAsync(
                    host,
                    sessionId,
                    options,
                    AcpSessionConfig.BuildProbeSelections(options, backend.SessionConfigSelections),
                    cancellationToken);
                options = applyResult.ConfigOptions;
                var optionCount = options.Count;
                var applyStatus = applyResult.SkippedCount == 0
                    ? $" · applied={applyResult.AppliedCount}"
                    : $" · applied={applyResult.AppliedCount} · skipped={applyResult.SkippedCount}";
                return new AcpBackendProbeResult(
                    true,
                    $"ACP 启动成功：{initialized.AgentName} {version} · configOptions={optionCount}{applyStatus}",
                    options,
                    applyResult.AppliedConfigIds);
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

        private static async Task<AcpBackendProbeConfigApplyResult> ApplyProbeSelectionsAsync(
            NodeAgentHost host,
            string sessionId,
            List<SessionConfigOptionDto> initialCatalog,
            List<AcpSessionConfigSelectionValue> selections,
            CancellationToken cancellationToken)
        {
            var catalog = initialCatalog ?? new List<SessionConfigOptionDto>();
            var pending = selections == null
                ? new List<AcpSessionConfigSelectionValue>()
                : new List<AcpSessionConfigSelectionValue>(selections);
            var appliedCount = 0;
            var skippedCount = 0;
            var appliedConfigIds = new List<string>();

            // 不使用固定次数上限。pending 中每项只会发送一次；响应目录中新出现但没有保存值的项
            // 只以 currentValue 合并到最终快照，不会重新入队。
            while (pending.Count > 0)
            {
                var attempted = false;
                for (var index = 0; index < pending.Count; index++)
                {
                    var selection = pending[index];
                    var option = AcpSessionConfig.FindOption(catalog, selection.ConfigId);
                    if (option == null) continue;

                    pending.RemoveAt(index);
                    if (!AcpSessionConfig.IsSelectionApplicable(option, selection, out _))
                    {
                        skippedCount++;
                        continue;
                    }

                    attempted = true;
                    try
                    {
                        var type = string.IsNullOrWhiteSpace(selection.Type) ? option.Type : selection.Type;
                        var setResponse = await host.SendAsync(
                            IpcMessageTypes.SetSessionConfigOption,
                            new SetSessionConfigOptionRequest
                            {
                                SessionId = sessionId,
                                ConfigId = selection.ConfigId,
                                Type = string.IsNullOrWhiteSpace(type) ? null : type,
                                Value = AcpSessionConfig.ValueToJsonElement(type, selection.Value)
                            },
                            cancellationToken);
                        if (setResponse.Type == IpcMessageTypes.Error)
                        {
                            skippedCount++;
                        }
                        else
                        {
                            var setResult = IpcJson.DeserializePayload<SetSessionConfigOptionResponse>(setResponse);
                            catalog = setResult.ConfigOptions ?? new List<SessionConfigOptionDto>();
                            appliedCount++;
                            appliedConfigIds.Add(selection.ConfigId);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // 单个配置失败不应使连通性探测失败；继续尝试同一快照中的其他配置。
                        System.Diagnostics.Debug.WriteLine(
                            $"[AcpBackendProbe] set_config_option failed configId={selection.ConfigId}: {FormatExceptionChain(ex)}");
                        skippedCount++;
                    }
                    break;
                }

                if (attempted) continue;
                skippedCount += pending.Count;
                break;
            }

            return new AcpBackendProbeConfigApplyResult(catalog, appliedCount, skippedCount, appliedConfigIds);
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
