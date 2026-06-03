using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RimWorldAgent.Core.CcbManager
{
    // ===== SdkSystemMessage 工厂（partial class） =====

    public abstract partial class SdkSystemMessage : SdkMessage
    {
        /// <summary>工厂：按 subtype 分发到具体子类</summary>
        public static SdkSystemMessage FromJson(string bodyJson, string rawJson)
        {
            string subtype;
            try
            {
                using var doc = JsonDocument.Parse(bodyJson);
                subtype = doc.RootElement.TryGetProperty("subtype", out var s) ? s.GetString() ?? "" : "";
            }
            catch
            {
                subtype = "";
            }

            SdkSystemMessage? msg = subtype switch
            {
                "init" => JsonSerializer.Deserialize<SdkSystemInitMessage>(bodyJson, SdkMessageSerializer.Options),
                "status" => JsonSerializer.Deserialize<SdkSystemStatusMessage>(bodyJson, SdkMessageSerializer.Options),
                "compact_boundary" => JsonSerializer.Deserialize<SdkSystemCompactBoundaryMessage>(bodyJson, SdkMessageSerializer.Options),
                "api_retry" => JsonSerializer.Deserialize<SdkApiRetryMessage>(bodyJson, SdkMessageSerializer.Options),
                "session_state_changed" => JsonSerializer.Deserialize<SdkSessionStateChangedMessage>(bodyJson, SdkMessageSerializer.Options),
                "post_turn_summary" => JsonSerializer.Deserialize<SdkPostTurnSummaryMessage>(bodyJson, SdkMessageSerializer.Options),
                "task_notification" => JsonSerializer.Deserialize<SdkTaskNotificationMessage>(bodyJson, SdkMessageSerializer.Options),
                "task_started" => JsonSerializer.Deserialize<SdkTaskStartedMessage>(bodyJson, SdkMessageSerializer.Options),
                "task_progress" => JsonSerializer.Deserialize<SdkTaskProgressMessage>(bodyJson, SdkMessageSerializer.Options),
                "hook_started" => JsonSerializer.Deserialize<SdkHookStartedMessage>(bodyJson, SdkMessageSerializer.Options),
                "hook_progress" => JsonSerializer.Deserialize<SdkHookProgressMessage>(bodyJson, SdkMessageSerializer.Options),
                "hook_response" => JsonSerializer.Deserialize<SdkHookResponseMessage>(bodyJson, SdkMessageSerializer.Options),
                "files_persisted" => JsonSerializer.Deserialize<SdkFilesPersistedMessage>(bodyJson, SdkMessageSerializer.Options),
                "local_command_output" => JsonSerializer.Deserialize<SdkLocalCommandOutputMessage>(bodyJson, SdkMessageSerializer.Options),
                "elicitation_complete" => JsonSerializer.Deserialize<SdkElicitationCompleteMessage>(bodyJson, SdkMessageSerializer.Options),
                _ => (SdkSystemMessage?)JsonSerializer.Deserialize<SdkSystemFallbackMessage>(bodyJson, SdkMessageSerializer.Options),
            };
            msg ??= new SdkSystemFallbackMessage(bodyJson, subtype);

            msg.RawJson = rawJson;
            SdkMessageSerializer.WarnExtra(msg, rawJson);
            return msg;
        }
    }

    // ===== init — 初始化配置 =====

    /// <summary>system.init — SDK 启动时发送，包含模型、权限、工具列表等运行环境信息。</summary>
    public class SdkSystemInitMessage : SdkSystemMessage
    {
        [JsonPropertyName("model")] public string? Model { get; set; }
        [JsonPropertyName("claude_code_version")] public string? ClaudeCodeVersion { get; set; }
        [JsonPropertyName("permissionMode")] public string? PermissionMode { get; set; }
        [JsonPropertyName("cwd")] public string? Cwd { get; set; }
        [JsonPropertyName("apiKeySource")] public string? ApiKeySource { get; set; }
        [JsonPropertyName("tools")] public List<string> Tools { get; set; } = new();
        [JsonPropertyName("skills")] public List<string> Skills { get; set; } = new();
        [JsonPropertyName("mcp_servers")] public List<SdkMcpServerInfo> McpServers { get; set; } = new();
        [JsonPropertyName("slash_commands")] public List<string> SlashCommands { get; set; } = new();
        [JsonPropertyName("output_style")] public string? OutputStyle { get; set; }
        [JsonPropertyName("agents")] public List<string> Agents { get; set; } = new();
        [JsonPropertyName("plugins")] public List<SdkPluginInfo> Plugins { get; set; } = new();
        [JsonPropertyName("betas")] public List<string> Betas { get; set; } = new();
        [JsonPropertyName("fast_mode_state")] public string? FastModeState { get; set; }
        [JsonPropertyName("analytics_disabled")] public bool? AnalyticsDisabled { get; set; }
        [JsonPropertyName("product_feedback_disabled")] public bool? ProductFeedbackDisabled { get; set; }
        [JsonPropertyName("memory_paths")] public SdkMemoryPaths? MemoryPaths { get; set; }
    }

    // ===== status — 运行状态变更 =====

    /// <summary>system.status — 运行状态变更（如 compacting 通知）。</summary>
    public class SdkSystemStatusMessage : SdkSystemMessage
    {
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("permissionMode")] public string? PermissionMode { get; set; }
    }

    // ===== compact_boundary — 上下文压缩分界 =====

    /// <summary>system.compact_boundary — 上下文压缩分界标记。</summary>
    public class SdkSystemCompactBoundaryMessage : SdkSystemMessage
    {
        [JsonPropertyName("compact_metadata")] public SdkCompactMetadata? CompactMetadata { get; set; }
    }

    // ===== api_retry — API 重试通知 =====

    /// <summary>system.api_retry — API 请求失败后将重试。</summary>
    public class SdkApiRetryMessage : SdkSystemMessage
    {
        [JsonPropertyName("attempt")] public int Attempt { get; set; }
        [JsonPropertyName("max_retries")] public int MaxRetries { get; set; }
        [JsonPropertyName("retry_delay_ms")] public double RetryDelayMs { get; set; }
        [JsonPropertyName("error_status")] public int? ErrorStatus { get; set; }
        [JsonPropertyName("error")] public string? Error { get; set; }
    }

    // ===== session_state_changed — 会话状态变更 =====

    /// <summary>system.session_state_changed — 会话状态变更通知（idle / running / requires_action）。</summary>
    public class SdkSessionStateChangedMessage : SdkSystemMessage
    {
        [JsonPropertyName("state")] public string State { get; set; } = "";
    }

    // ===== post_turn_summary — 轮次后摘要 =====

    /// <summary>system.post_turn_summary — 每轮 assistant 结束后的背景摘要（@internal）。</summary>
    public class SdkPostTurnSummaryMessage : SdkSystemMessage
    {
        [JsonPropertyName("summarizes_uuid")] public string SummarizesUuid { get; set; } = "";
        [JsonPropertyName("status_category")] public string StatusCategory { get; set; } = "";
        [JsonPropertyName("status_detail")] public string StatusDetail { get; set; } = "";
        [JsonPropertyName("is_noteworthy")] public bool IsNoteworthy { get; set; }
        [JsonPropertyName("title")] public string Title { get; set; } = "";
        [JsonPropertyName("description")] public string Description { get; set; } = "";
        [JsonPropertyName("recent_action")] public string RecentAction { get; set; } = "";
        [JsonPropertyName("needs_action")] public string NeedsAction { get; set; } = "";
        [JsonPropertyName("artifact_urls")] public List<string> ArtifactUrls { get; set; } = new();
    }

    // ===== task_notification — 后台任务完成 =====

    /// <summary>system.task_notification — 后台任务完成/失败通知。</summary>
    public class SdkTaskNotificationMessage : SdkSystemMessage
    {
        [JsonPropertyName("task_id")] public string TaskId { get; set; } = "";
        [JsonPropertyName("tool_use_id")] public string? ToolUseId { get; set; }
        [JsonPropertyName("status")] public string Status { get; set; } = "";
        [JsonPropertyName("output_file")] public string OutputFile { get; set; } = "";
        [JsonPropertyName("summary")] public string Summary { get; set; } = "";
        [JsonPropertyName("usage")] public SdkTaskUsage? Usage { get; set; }
    }

    // ===== task_started — 后台任务启动 =====

    /// <summary>system.task_started — 后台任务启动通知。</summary>
    public class SdkTaskStartedMessage : SdkSystemMessage
    {
        [JsonPropertyName("task_id")] public string TaskId { get; set; } = "";
        [JsonPropertyName("tool_use_id")] public string? ToolUseId { get; set; }
        [JsonPropertyName("description")] public string Description { get; set; } = "";
        [JsonPropertyName("task_type")] public string? TaskType { get; set; }
        [JsonPropertyName("workflow_name")] public string? WorkflowName { get; set; }
        [JsonPropertyName("prompt")] public string? Prompt { get; set; }
    }

    // ===== task_progress — 后台任务进度 =====

    /// <summary>system.task_progress — 后台任务进度更新。</summary>
    public class SdkTaskProgressMessage : SdkSystemMessage
    {
        [JsonPropertyName("task_id")] public string TaskId { get; set; } = "";
        [JsonPropertyName("tool_use_id")] public string? ToolUseId { get; set; }
        [JsonPropertyName("description")] public string Description { get; set; } = "";
        [JsonPropertyName("usage")] public SdkTaskUsage Usage { get; set; } = new();
        [JsonPropertyName("last_tool_name")] public string? LastToolName { get; set; }
        [JsonPropertyName("summary")] public string? Summary { get; set; }
    }

    // ===== hook_started =====

    /// <summary>system.hook_started — Hook 开始执行。</summary>
    public class SdkHookStartedMessage : SdkSystemMessage
    {
        [JsonPropertyName("hook_id")] public string HookId { get; set; } = "";
        [JsonPropertyName("hook_name")] public string HookName { get; set; } = "";
        [JsonPropertyName("hook_event")] public string HookEvent { get; set; } = "";
    }

    // ===== hook_progress =====

    /// <summary>system.hook_progress — Hook 执行进度（stdout/stderr 增量）。</summary>
    public class SdkHookProgressMessage : SdkSystemMessage
    {
        [JsonPropertyName("hook_id")] public string HookId { get; set; } = "";
        [JsonPropertyName("hook_name")] public string HookName { get; set; } = "";
        [JsonPropertyName("hook_event")] public string HookEvent { get; set; } = "";
        [JsonPropertyName("stdout")] public string Stdout { get; set; } = "";
        [JsonPropertyName("stderr")] public string Stderr { get; set; } = "";
        [JsonPropertyName("output")] public string Output { get; set; } = "";
    }

    // ===== hook_response =====

    /// <summary>system.hook_response — Hook 执行完成。</summary>
    public class SdkHookResponseMessage : SdkSystemMessage
    {
        [JsonPropertyName("hook_id")] public string HookId { get; set; } = "";
        [JsonPropertyName("hook_name")] public string HookName { get; set; } = "";
        [JsonPropertyName("hook_event")] public string HookEvent { get; set; } = "";
        [JsonPropertyName("output")] public string Output { get; set; } = "";
        [JsonPropertyName("stdout")] public string Stdout { get; set; } = "";
        [JsonPropertyName("stderr")] public string Stderr { get; set; } = "";
        [JsonPropertyName("exit_code")] public int? ExitCode { get; set; }
        [JsonPropertyName("outcome")] public string Outcome { get; set; } = "";
    }

    // ===== files_persisted =====

    /// <summary>system.files_persisted — 文件持久化通知。</summary>
    public class SdkFilesPersistedMessage : SdkSystemMessage
    {
        [JsonPropertyName("files")] public List<SdkPersistedFile> Files { get; set; } = new();
        [JsonPropertyName("failed")] public List<SdkFailedFile> Failed { get; set; } = new();
        [JsonPropertyName("processed_at")] public string ProcessedAt { get; set; } = "";
    }

    // ===== local_command_output =====

    /// <summary>system.local_command_output — 本地 slash command 输出。</summary>
    public class SdkLocalCommandOutputMessage : SdkSystemMessage
    {
        [JsonPropertyName("content")] public string Content { get; set; } = "";
    }

    // ===== elicitation_complete =====

    /// <summary>system.elicitation_complete — MCP elicitation 完成。</summary>
    public class SdkElicitationCompleteMessage : SdkSystemMessage
    {
        [JsonPropertyName("mcp_server_name")] public string McpServerName { get; set; } = "";
        [JsonPropertyName("elicitation_id")] public string ElicitationId { get; set; } = "";
    }

    // ===== fallback — 未知 subtype 兜底 =====

    /// <summary>未知 system subtype 兜底消息。</summary>
    public class SdkSystemFallbackMessage : SdkSystemMessage
    {
        public string RawBody { get; }
        public SdkSystemFallbackMessage(string rawBody, string subtype)
        {
            RawBody = rawBody;
            Subtype = subtype;
            RawJson = rawBody;
        }
    }

    // ===== 辅助类型 =====

    /// <summary>后台任务 Token 使用统计（task_notification / task_progress 的 usage 字段）。</summary>
    public class SdkTaskUsage
    {
        [JsonPropertyName("total_tokens")] public long TotalTokens { get; set; }
        [JsonPropertyName("tool_uses")] public int ToolUses { get; set; }
        [JsonPropertyName("duration_ms")] public long DurationMs { get; set; }
    }

    /// <summary>files_persisted 成功持久化的文件。</summary>
    public class SdkPersistedFile
    {
        [JsonPropertyName("filename")] public string Filename { get; set; } = "";
        [JsonPropertyName("file_id")] public string FileId { get; set; } = "";
    }

    /// <summary>files_persisted 持久化失败的文件。</summary>
    public class SdkFailedFile
    {
        [JsonPropertyName("filename")] public string Filename { get; set; } = "";
        [JsonPropertyName("error")] public string Error { get; set; } = "";
    }
}
