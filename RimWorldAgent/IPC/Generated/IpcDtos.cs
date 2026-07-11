using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RimWorldAgent.IPC.Generated
{
    public static class IpcMessageTypes
    {
        public const string Initialize = "initialize";
        public const string InitializeResponse = "initialize_response";
        public const string NewSession = "new_session";
        public const string NewSessionResponse = "new_session_response";
        public const string ResumeSession = "resume_session";
        public const string ResumeSessionResponse = "resume_session_response";
        public const string LoadSession = "load_session";
        public const string LoadSessionResponse = "load_session_response";
        public const string SetSessionConfigOption = "set_session_config_option";
        public const string SetSessionConfigOptionResponse = "set_session_config_option_response";
        public const string Prompt = "prompt";
        public const string PromptResponse = "prompt_response";
        public const string Cancel = "cancel";
        public const string CancelResponse = "cancel_response";
        public const string Close = "close";
        public const string CloseResponse = "close_response";
        public const string Event = "event";
        public const string Error = "error";
    }

    public sealed class IpcEnvelope
    {
        [JsonPropertyName("protocol")]
        public string Protocol { get; set; } = "rimworld-agent-ipc";

        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("type")]
        public string Type { get; set; } = "";

        [JsonPropertyName("requestId")]
        public string? RequestId { get; set; }

        [JsonPropertyName("payload")]
        public JsonElement? Payload { get; set; }

        [JsonPropertyName("meta")]
        public Dictionary<string, JsonElement>? Meta { get; set; }
    }

    public sealed class InitializeRequest
    {
        [JsonPropertyName("hostVersion")]
        public string HostVersion { get; set; } = "1.0.0";

        [JsonPropertyName("config")]
        public AgentRuntimeConfig Config { get; set; } = new AgentRuntimeConfig();
    }

    public sealed class InitializeResponse
    {
        [JsonPropertyName("protocolVersion")]
        public int ProtocolVersion { get; set; } = 1;

        [JsonPropertyName("agentName")]
        public string AgentName { get; set; } = "";

        [JsonPropertyName("agentVersion")]
        public string? AgentVersion { get; set; }

        [JsonPropertyName("loadSession")]
        public bool LoadSession { get; set; }

        [JsonPropertyName("resumeSession")]
        public bool ResumeSession { get; set; }
    }

    public sealed class SessionRequest
    {
        [JsonPropertyName("sessionId")]
        public string SessionId { get; set; } = "";
    }

    public sealed class SessionResponse
    {
        [JsonPropertyName("sessionId")]
        public string SessionId { get; set; } = "";

        [JsonPropertyName("configOptions")]
        public List<SessionConfigOptionDto>? ConfigOptions { get; set; }
    }

    public sealed class SessionConfigOptionDto
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("category")]
        public string? Category { get; set; }

        [JsonPropertyName("type")]
        public string Type { get; set; } = "select";

        [JsonPropertyName("currentValue")]
        public JsonElement? CurrentValue { get; set; }

        [JsonPropertyName("options")]
        public List<JsonElement>? Options { get; set; }
    }

    public sealed class SetSessionConfigOptionRequest
    {
        [JsonPropertyName("sessionId")]
        public string SessionId { get; set; } = "";

        [JsonPropertyName("configId")]
        public string ConfigId { get; set; } = "";

        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("value")]
        public JsonElement Value { get; set; }
    }

    public sealed class SetSessionConfigOptionResponse
    {
        [JsonPropertyName("sessionId")]
        public string SessionId { get; set; } = "";

        [JsonPropertyName("configOptions")]
        public List<SessionConfigOptionDto> ConfigOptions { get; set; } = new List<SessionConfigOptionDto>();
    }

    public sealed class PromptRequest
    {
        [JsonPropertyName("sessionId")]
        public string SessionId { get; set; } = "";

        [JsonPropertyName("prompt")]
        public string Prompt { get; set; } = "";
    }

    public sealed class PromptResponse
    {
        [JsonPropertyName("sessionId")]
        public string SessionId { get; set; } = "";

        [JsonPropertyName("stopReason")]
        public string StopReason { get; set; } = "";

        [JsonPropertyName("status")]
        public string Status { get; set; } = "success";

        [JsonPropertyName("inputTokens")]
        public long? InputTokens { get; set; }

        [JsonPropertyName("outputTokens")]
        public long? OutputTokens { get; set; }

        [JsonPropertyName("cacheReadTokens")]
        public long? CacheReadTokens { get; set; }

        [JsonPropertyName("cacheCreateTokens")]
        public long? CacheCreateTokens { get; set; }
    }

    public sealed class CancelResponse
    {
        [JsonPropertyName("sessionId")]
        public string SessionId { get; set; } = "";

        [JsonPropertyName("cancelled")]
        public bool Cancelled { get; set; }
    }

    public sealed class CloseResponse
    {
        [JsonPropertyName("closed")]
        public bool Closed { get; set; }
    }

    public sealed class ErrorResponse
    {
        [JsonPropertyName("code")]
        public string Code { get; set; } = "agent_error";

        [JsonPropertyName("message")]
        public string Message { get; set; } = "";
    }

    public sealed class AgentEvent
    {
        [JsonPropertyName("kind")]
        public string Kind { get; set; } = "";

        [JsonPropertyName("sessionId")]
        public string? SessionId { get; set; }

        [JsonPropertyName("messageId")]
        public string? MessageId { get; set; }

        [JsonPropertyName("text")]
        public string? Text { get; set; }

        [JsonPropertyName("toolCallId")]
        public string? ToolCallId { get; set; }

        [JsonPropertyName("toolName")]
        public string? ToolName { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("toolKind")]
        public string? ToolKind { get; set; }

        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("rawInput")]
        public JsonElement? RawInput { get; set; }

        [JsonPropertyName("rawOutput")]
        public JsonElement? RawOutput { get; set; }

        [JsonPropertyName("isError")]
        public bool? IsError { get; set; }

        [JsonPropertyName("inputTokens")]
        public long? InputTokens { get; set; }

        [JsonPropertyName("outputTokens")]
        public long? OutputTokens { get; set; }

        [JsonPropertyName("cacheReadTokens")]
        public long? CacheReadTokens { get; set; }

        [JsonPropertyName("cacheCreateTokens")]
        public long? CacheCreateTokens { get; set; }

        [JsonPropertyName("contextWindow")]
        public long? ContextWindow { get; set; }

        [JsonPropertyName("usedTokens")]
        public long? UsedTokens { get; set; }

        [JsonPropertyName("sizeTokens")]
        public long? SizeTokens { get; set; }

        [JsonPropertyName("titleText")]
        public string? TitleText { get; set; }
    }

    public sealed class AgentRuntimeConfig
    {
        [JsonPropertyName("backend")]
        public BackendLaunch Backend { get; set; } = new BackendLaunch();

        [JsonPropertyName("cwd")]
        public string Cwd { get; set; } = "";

        [JsonPropertyName("additionalDirectories")]
        public List<string> AdditionalDirectories { get; set; } = new List<string>();

        [JsonPropertyName("prompt")]
        public PromptConfig Prompt { get; set; } = new PromptConfig();

        [JsonPropertyName("agentMcpUrl")]
        public string AgentMcpUrl { get; set; } = "";
    }

    public sealed class BackendLaunch
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "claude";

        [JsonPropertyName("command")]
        public string Command { get; set; } = "node";

        [JsonPropertyName("args")]
        public List<string> Args { get; set; } = new List<string>();

        [JsonPropertyName("workingDirectory")]
        public string WorkingDirectory { get; set; } = "";

        [JsonPropertyName("environment")]
        public Dictionary<string, string> Environment { get; set; } = new Dictionary<string, string>();
    }

    public sealed class PromptConfig
    {
        [JsonPropertyName("systemPrompt")]
        public string SystemPrompt { get; set; } = "";
    }

}
