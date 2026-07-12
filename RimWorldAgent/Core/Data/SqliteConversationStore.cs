using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using RimWorldAgent.Core.AgentRuntime;

namespace RimWorldAgent.Core.Data
{
    /// <summary>
    /// SQLite 持久化会话存储。
    /// 基于 Microsoft.Data.Sqlite — 官方 ADO.NET 提供器，跨平台。
    /// </summary>
    public sealed class SqliteConversationStore : IConversationStore, IDisposable
    {
        private readonly string _connectionString;
        private readonly string _saveId;
        private readonly object _writeLock = new();
        private bool _disposed;

        /// <param name="filePath">SQLite 文件路径，如 .../conversation.db</param>
        /// <param name="saveId">存档标识 — 所有查询/写入均按此 ID 隔离</param>
        public SqliteConversationStore(string filePath, string saveId)
        {
            if (string.IsNullOrEmpty(saveId))
                throw new ArgumentNullException(nameof(saveId));

            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            _saveId = saveId;
            _connectionString = $"Data Source={filePath}";
            CoreLog.Info($"[SqliteConvStore] DB: {filePath}  save_id={_saveId}");
            InitTable();
        }

        private void InitTable()
        {
            try
            {
                using var conn = OpenConnection();
                using var cmd = new SqliteCommand(
                    @"CREATE TABLE IF NOT EXISTS conversation (
                        id          INTEGER PRIMARY KEY AUTOINCREMENT,
                        role        TEXT    NOT NULL,
                        text        TEXT    NOT NULL DEFAULT '',
                        thinking    TEXT    NOT NULL DEFAULT '',
                        run_id      TEXT    NOT NULL DEFAULT '',
                        agent_type  TEXT    NOT NULL DEFAULT '',
                        tool_name   TEXT    NOT NULL DEFAULT '',
                        tool_input  TEXT    NOT NULL DEFAULT '',
                        is_tool_error INTEGER NOT NULL DEFAULT 0,
                        tool_duration_ms REAL NOT NULL DEFAULT 0,
                        timestamp   TEXT    NOT NULL,
                        game_day    INTEGER NOT NULL DEFAULT 0,
                        save_id     TEXT    NOT NULL
                    );
                    CREATE INDEX IF NOT EXISTS idx_timestamp ON conversation(timestamp);
                    CREATE INDEX IF NOT EXISTS idx_save_id ON conversation(save_id);
                    CREATE INDEX IF NOT EXISTS idx_game_day ON conversation(game_day);
                    CREATE INDEX IF NOT EXISTS idx_tool_call_id ON conversation(run_id);
                    -- tool_permission: 独立表，记录 toolCallId -> 权限判定用工具名
                    CREATE TABLE IF NOT EXISTS tool_permission (
                        id              INTEGER PRIMARY KEY AUTOINCREMENT,
                        tool_call_id    TEXT    NOT NULL,
                        permission_tool_name TEXT NOT NULL DEFAULT '',
                        save_id         TEXT    NOT NULL,
                        timestamp       TEXT    NOT NULL,
                        UNIQUE(tool_call_id, save_id)
                    );
                    CREATE INDEX IF NOT EXISTS idx_tool_perm_call_id ON tool_permission(tool_call_id);", conn);
                cmd.ExecuteNonQuery();
            }

            catch (Exception ex)

            {

                CoreLog.Error($"[SqliteConvStore] 建表失败: {ex.Message}");
                throw;
            }
        }

        public int Count
        {
            get
            {
                if (_disposed) return 0;
                try
                {
                    using var conn = OpenConnection();
                    using var cmd = new SqliteCommand(
                        "SELECT COUNT(*) FROM conversation WHERE save_id = @saveId", conn);
                    cmd.Parameters.AddWithValue("@saveId", _saveId);
                    return Convert.ToInt32(cmd.ExecuteScalar());
                }
                catch (Exception ex)
                {
                    CoreLog.Warn($"[SqliteConvStore] Count 查询失败: {ex.Message}");
                    return 0;
                }
            }
        }

        public void RecordUserMessage(string text)
            => Record(ConvRole.User, text, "", "", "");
        public void RecordAssistantMessage(string text, string thinking, string runId, string agentType)
            => Record(ConvRole.Assistant, text, thinking, runId, agentType);
        public void RecordSystemMessage(string text)
            => Record(ConvRole.System, text, "", "", "");
        public void RecordToolCall(string toolId, string name, string input, string permissionToolName = "")
        {
            Record(ConvRole.ToolCall, "", "", toolId ?? "",
                agentType: "", toolName: name ?? "", toolInput: input ?? "");
            UpsertToolPermission(toolId ?? "", permissionToolName ?? "");
        }
        public void RecordToolResult(string toolId, bool isError, double durationMs, string output)
            => Record(ConvRole.ToolResult, output ?? "", "", toolId ?? "",
                agentType: "", isToolError: isError, toolDurationMs: durationMs);

        private void Record(ConvRole role, string text, string thinking, string runId, string agentType,
            string toolName = "", string toolInput = "", bool isToolError = false, double toolDurationMs = 0)
        {
            if (_disposed) return;
            lock (_writeLock)
            {
                try
                {
                    using var conn = OpenConnection();
                    using var cmd = new SqliteCommand(
                        @"INSERT INTO conversation (role, text, thinking, run_id, agent_type, tool_name, tool_input, is_tool_error, tool_duration_ms, timestamp, game_day, save_id)
                          VALUES (@role, @text, @thinking, @runId, @agentType, @toolName, @toolInput, @isToolError, @toolDurationMs, @ts, @gameDay, @saveId)", conn);
                    cmd.Parameters.AddWithValue("@role", RoleToString(role));
                    cmd.Parameters.AddWithValue("@text", text ?? "");
                    cmd.Parameters.AddWithValue("@thinking", thinking ?? "");
                    cmd.Parameters.AddWithValue("@runId", runId ?? "");
                    cmd.Parameters.AddWithValue("@agentType", agentType ?? "");
                    cmd.Parameters.AddWithValue("@toolName", toolName ?? "");
                    cmd.Parameters.AddWithValue("@toolInput", toolInput ?? "");
                    cmd.Parameters.AddWithValue("@isToolError", isToolError ? 1 : 0);
                    cmd.Parameters.AddWithValue("@toolDurationMs", toolDurationMs);
                    cmd.Parameters.AddWithValue("@ts", DateTime.UtcNow.ToString("o"));
                    cmd.Parameters.AddWithValue("@gameDay", AgentOrchestrator.GameDay);
                    cmd.Parameters.AddWithValue("@saveId", _saveId);
                    cmd.ExecuteNonQuery();
                }
                catch (Exception ex)
                {
                    CoreLog.Warn($"[SqliteConvStore] 写入失败: {ex.Message}");
                }
            }
        }

        public string? GetPermissionToolName(string toolCallId)
        {
            if (_disposed || string.IsNullOrWhiteSpace(toolCallId)) return null;
            try
            {
                using var conn = OpenConnection();
                using var cmd = new SqliteCommand(
                    "SELECT permission_tool_name FROM tool_permission WHERE save_id = @saveId AND tool_call_id = @toolCallId ORDER BY id DESC LIMIT 1", conn);
                cmd.Parameters.AddWithValue("@saveId", _saveId);
                cmd.Parameters.AddWithValue("@toolCallId", toolCallId);
                var result = cmd.ExecuteScalar();
                return result == null || result == DBNull.Value ? null : Convert.ToString(result);
            }
            catch (Exception ex)
            {
                CoreLog.Warn($"[SqliteConvStore] GetPermissionToolName 失败: {ex.Message}");
                return null;
            }
        }

        private void UpsertToolPermission(string toolCallId, string permissionToolName)
        {
            if (_disposed || string.IsNullOrWhiteSpace(toolCallId) || string.IsNullOrWhiteSpace(permissionToolName)) return;
            lock (_writeLock)
            {
                try
                {
                    using var conn = OpenConnection();
                    using var cmd = new SqliteCommand(
                        @"INSERT OR REPLACE INTO tool_permission (tool_call_id, permission_tool_name, save_id, timestamp)
                          VALUES (@toolCallId, @permName, @saveId, @ts)", conn);
                    cmd.Parameters.AddWithValue("@toolCallId", toolCallId);
                    cmd.Parameters.AddWithValue("@permName", permissionToolName);
                    cmd.Parameters.AddWithValue("@saveId", _saveId);
                    cmd.Parameters.AddWithValue("@ts", DateTime.UtcNow.ToString("o"));
                    cmd.ExecuteNonQuery();
                }
                catch (Exception ex)
                {
                    CoreLog.Warn($"[SqliteConvStore] UpsertToolPermission 失败: {ex.Message}");
                }
            }
        }

        public ConversationEntry? GetAt(long id)
            => QueryOne("SELECT * FROM conversation WHERE save_id = @saveId AND id = @id",
                ("@id", (object)id));

        public IReadOnlyList<ConversationEntry> GetRecent(int n)
            => QueryList(
                "SELECT * FROM conversation WHERE save_id = @saveId ORDER BY id DESC LIMIT @n",
                ("@n", (object)Math.Max(1, n)));

        public IReadOnlyList<ConversationEntry> GetBefore(long beforeId, int n)
            => QueryList(
                "SELECT * FROM conversation WHERE save_id = @saveId AND id < @beforeId ORDER BY id DESC LIMIT @n",
                ("@beforeId", (object)beforeId), ("@n", (object)Math.Max(1, n)));

        public IReadOnlyList<ConversationEntry> QueryToolCalls(
            string? toolName = null, int fromDay = 0, int toDay = int.MaxValue,
            int limit = 100, long beforeId = long.MaxValue)
        {
            if (_disposed) return Array.Empty<ConversationEntry>();
            try
            {
                var sql = @"SELECT * FROM conversation
                    WHERE save_id = @saveId
                      AND role = 'tool_call'
                      AND (@tool IS NULL OR tool_name = @tool)
                      AND (@fromDay = 0 OR game_day >= @fromDay)
                      AND (@toDay = 2147483647 OR game_day <= @toDay)
                      AND id < @beforeId
                    ORDER BY id DESC
                    LIMIT @limit";

                using var conn = OpenConnection();
                using var cmd = new SqliteCommand(sql, conn);
                cmd.Parameters.AddWithValue("@saveId", _saveId);
                cmd.Parameters.AddWithValue("@tool", (object?)toolName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@fromDay", fromDay);
                cmd.Parameters.AddWithValue("@toDay", toDay);
                cmd.Parameters.AddWithValue("@beforeId", beforeId);
                cmd.Parameters.AddWithValue("@limit", Math.Max(1, limit));

                var list = new List<ConversationEntry>();
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) list.Add(ReadEntry(reader));
                list.Reverse();
                return list;
            }
            catch (Exception ex)
            {
                CoreLog.Warn($"[SqliteConvStore] QueryToolCalls 失败: {ex.Message}");
                return Array.Empty<ConversationEntry>();
            }
        }

        public IReadOnlyList<ToolCallDailyStat> GetToolDailyStats(
            int fromDay = 0, int toDay = int.MaxValue)
        {
            if (_disposed) return Array.Empty<ToolCallDailyStat>();
            try
            {
                var sql = @"SELECT game_day, tool_name, COUNT(*) AS call_count
                    FROM conversation
                    WHERE save_id = @saveId
                      AND role = 'tool_call'
                      AND (@fromDay = 0 OR game_day >= @fromDay)
                      AND (@toDay = 2147483647 OR game_day <= @toDay)
                    GROUP BY game_day, tool_name
                    ORDER BY game_day DESC, call_count DESC";

                using var conn = OpenConnection();
                using var cmd = new SqliteCommand(sql, conn);
                cmd.Parameters.AddWithValue("@saveId", _saveId);
                cmd.Parameters.AddWithValue("@fromDay", fromDay);
                cmd.Parameters.AddWithValue("@toDay", toDay);

                var list = new List<ToolCallDailyStat>();
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    list.Add(new ToolCallDailyStat
                    {
                        GameDay = reader.GetInt32(0),
                        ToolName = reader.GetString(1),
                        CallCount = reader.GetInt32(2)
                    });
                }
                return list;
            }
            catch (Exception ex)
            {
                CoreLog.Warn($"[SqliteConvStore] GetToolDailyStats 失败: {ex.Message}");
                return Array.Empty<ToolCallDailyStat>();
            }
        }

        public List<string> GetKnownToolNames()
        {
            if (_disposed) return new List<string>();
            try
            {
                using var conn = OpenConnection();
                using var cmd = new SqliteCommand(
                    @"SELECT DISTINCT tool_name FROM conversation
                      WHERE save_id = @saveId AND role = 'tool_call' AND tool_name != ''
                      ORDER BY tool_name", conn);
                cmd.Parameters.AddWithValue("@saveId", _saveId);
                var list = new List<string>();
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) list.Add(reader.GetString(0));
                return list;
            }
            catch (Exception ex)
            {
                CoreLog.Warn($"[SqliteConvStore] GetKnownToolNames 失败: {ex.Message}");
                return new List<string>();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
        }

        // ===== Helpers =====

        private static ConversationEntry ReadEntry(SqliteDataReader reader)
        {
            return new ConversationEntry
            {
                Id = reader.GetInt64(0),
                Role = StringToRole(reader.GetString(1)),
                Text = reader.GetString(2),
                Thinking = reader.GetString(3),
                RunId = reader.GetString(4),
                AgentType = reader.GetString(5),
                ToolName = reader.GetString(6),
                ToolInput = reader.GetString(7),
                IsToolError = reader.GetInt32(8) != 0,
                ToolDurationMs = reader.GetDouble(9),
                Timestamp = DateTime.TryParse(reader.GetString(10), out var ts) ? ts : DateTime.UtcNow,
                GameDay = reader.GetInt32(11)
            };
        }

        private ConversationEntry? QueryOne(string sql, params (string, object)[] args)
        {
            if (_disposed) return null;
            try
            {
                using var conn = OpenConnection();
                using var cmd = new SqliteCommand(sql, conn);
                cmd.Parameters.AddWithValue("@saveId", _saveId);
                foreach (var (name, val) in args)
                    cmd.Parameters.AddWithValue(name, val);
                using var reader = cmd.ExecuteReader();
                return reader.Read() ? ReadEntry(reader) : null;
            }
            catch (Exception ex)
            {
                CoreLog.Warn($"[SqliteConvStore] QueryOne 失败: {ex.Message}");
                return null;
            }
        }

        private IReadOnlyList<ConversationEntry> QueryList(string sql, params (string, object)[] args)
        {
            if (_disposed) return Array.Empty<ConversationEntry>();
            try
            {
                using var conn = OpenConnection();
                using var cmd = new SqliteCommand(sql, conn);
                cmd.Parameters.AddWithValue("@saveId", _saveId);
                foreach (var (name, val) in args)
                    cmd.Parameters.AddWithValue(name, val);
                var list = new List<ConversationEntry>();
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) list.Add(ReadEntry(reader));
                list.Reverse();
                return list;
            }
            catch (Exception ex)
            {
                CoreLog.Warn($"[SqliteConvStore] QueryList 失败: {ex.Message}");
                return Array.Empty<ConversationEntry>();
            }
        }

        private SqliteConnection OpenConnection()
        {
            var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using (var pragma = new SqliteCommand("PRAGMA journal_mode=WAL", conn))
                pragma.ExecuteNonQuery();
            return conn;
        }

        private static string RoleToString(ConvRole role) => role switch
        {
            ConvRole.User => "user",
            ConvRole.Assistant => "assistant",
            ConvRole.System => "system",
            ConvRole.ToolCall => "tool_call",
            ConvRole.ToolResult => "tool_result",
            _ => "unknown"
        };

        private static ConvRole StringToRole(string s) => s switch
        {
            "user" => ConvRole.User,
            "assistant" => ConvRole.Assistant,
            "system" => ConvRole.System,
            "tool_call" => ConvRole.ToolCall,
            "tool_result" => ConvRole.ToolResult,
            _ => ConvRole.System
        };
    }
}
