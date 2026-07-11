using System;
using System.Collections.Generic;

namespace RimWorldAgent.Core.AgentTransport
{
    /// <summary>
    /// ACP Backend 启动描述。由 ModSettings 组装后交给 AgentEngine，
    /// 不包含 ACP 协议对象，也不负责持久化。
    /// </summary>
    public sealed class AcpAgentServerDefinition
    {
        public string Name { get; set; } = "";
        public string Command { get; set; } = "";
        public List<string> Args { get; set; } = new List<string>();
        public Dictionary<string, string> Env { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string? WorkingDirectory { get; set; }
    }

}
