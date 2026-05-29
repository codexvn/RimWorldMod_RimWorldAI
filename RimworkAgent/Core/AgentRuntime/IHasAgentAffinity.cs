namespace RimworkAgent.Core.AgentRuntime
{
    [System.Flags]
    public enum AgentAffinity { Overseer = 1, Economy = 2, Combat = 4, Medic = 8 }
    public interface IHasAgentAffinity { AgentAffinity AgentAffinity { get; } }
}
