namespace RimWorldMCP.AgentRuntime
{
    /// <summary>可选接口：实现此接口的工具受限为指定 Agent 可见。未实现的工具对所有 Agent 可见。</summary>
    public interface IHasAgentAffinity
    {
        AgentAffinity AgentAffinity { get; }
    }
}
