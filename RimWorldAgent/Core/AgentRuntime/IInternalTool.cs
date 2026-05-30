using System.Text.Json;
using System.Threading.Tasks;

namespace RimWorldAgent.Core.AgentRuntime
{
    /// <summary>Agent 内部 Tool 接口。每个实现一个独立类，通过反射自动注册到 InternalToolRegistry。</summary>
    public interface IInternalTool
    {
        string Name { get; }
        string Description { get; }
        JsonElement InputSchema { get; }
        /// <summary>执行工具。返回 (resultText, shouldExitSession)。</summary>
        Task<(string result, bool exit)> ExecuteAsync(JsonElement? args);
    }
}
