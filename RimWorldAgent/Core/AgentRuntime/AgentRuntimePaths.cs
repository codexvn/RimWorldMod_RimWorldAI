using System.IO;

namespace RimWorldAgent.Core.AgentRuntime
{
    internal static class AgentRuntimePaths
    {
        public const string NodeHostDirectoryName = "rimworld-acp-host";
        public const string NodeHostDefaultEntryPoint = "dist/main.js";
        public const string SessionsDirectoryName = "claude-sessions";
        public const string DefaultProjectDirectoryName = "rimworld-agent";
        public const string StandaloneProjectDirectoryName = "dev-session";
        public const string ProbeProjectDirectoryName = "acp-probe";
        public const string BuiltinSkillsDirectoryName = "Skills";
        public const string UserSkillsDirectoryName = "Skills.d";
        public const string PromptFileName = "Prompt.md";
        public const string SkillsDescriptionFileName = "skills-desc.txt";
        public const string ConversationDatabaseFileName = "conversation.db";
        public const string SessionIdFileName = "session-id.txt";
        public const string TokenUsageDatabaseFileName = "RimWorldMCP_Token.json";
        public const string NodeCommandName = "node";
        public const string NodeJsCommandName = "nodejs";
        public const string NodeDirectoryName = "nodejs";
        public const string NodeExecutableName = "node.exe";
        public const string NodeJsExecutableName = "nodejs.exe";
        public const string NvmDirectoryName = "nvm";
        public const string NativeDirectoryName = "Native";

        public static string GetNodeHostEntryPoint(string hostDirectory, string? entryPoint = null)
        {
            var relativePath = string.IsNullOrWhiteSpace(entryPoint)
                ? NodeHostDefaultEntryPoint
                : entryPoint!;
            return Path.Combine(hostDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        }

        public static string GetDefaultProjectDirectory(string baseDirectory)
            => Path.Combine(baseDirectory, SessionsDirectoryName, DefaultProjectDirectoryName);

        public static string GetStandaloneProjectDirectory(string baseDirectory)
            => Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", SessionsDirectoryName, StandaloneProjectDirectoryName));

        public static string GetProbeProjectDirectory(string baseDirectory)
            => Path.Combine(baseDirectory, SessionsDirectoryName, ProbeProjectDirectoryName);

        public static string GetAgentSourceNodeHostDirectory(string baseDirectory)
            => Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "RimWorldAgent", "Node", NodeHostDirectoryName));

        public static string GetStandaloneSourceNodeHostDirectory(string baseDirectory)
            => Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", "RimWorldAgent", "Node", NodeHostDirectoryName));
    }
}
