using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using RimWorldAgent.Core.AgentRuntime;

namespace RimWorldAgent.Core.Data
{
    /// <summary>
    /// SQLite 持久化会话存储 — EXE 模式使用。
    /// WAL 模式，支持多读单写并发。可视化：sqlite3 conversation.db
    /// </summary>
    public sealed class SqliteConversationStore : IConversationStore, IDisposable
    {
        private readonly string _connectionString;
        private readonly object _writeLock = new();
        private bool _disposed;

        /// <param name="filePath">SQLite 文件路径，如 .../conversation.db</param>
        public SqliteConversationStore(string filePath)
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            _connectionString = $"Data Source={filePath};Version=3;Journal Mode=WAL;";
            CoreLog.Info($"[SqliteConvStore] DB: {filePath}");
            InitTable();
        }

        public int Count
        {
            get
            {
                if (_disposed) return 0;
                try
                {
                    using var conn = OpenConnection();
                    using var cmd = new SQLiteCommand("SELECT COUNT(*) FROM conversation", conn);
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
        {
            Record(ConvRole.User, text, "", "", "");
        }

        public void RecordAssistantMessage(string text, string thinking, string runId, string agentType)
        {
            Record(ConvRole.Assistant, text, thinking, runId, agentType);
        }

        public void RecordSystemMessage(string text)
        {
            Record(ConvRole.System, text, "", "", "");
        }

        public void RecordToolCall(string toolId, string name, string input)
        {
            Record(ConvRole.ToolCall, "", "", toolId ?? "",
                agentType: "", toolName: name ?? "", toolInput: input ?? "");
        }

        public void RecordToolResult(string toolId, bool isError, double durationMs, string output)
        {
            Record(ConvRole.ToolResult, output ?? "", "", toolId ?? "",
                agentType: "", isToolError: isError, toolDurationMs: durationMs);
        }

        private void Record(ConvRole role, string text, string thinking, string runId, string agentType,
            string toolName = "", string toolInput = "", bool isToolError = false, double toolDurationMs = 0)
        {
            if (_disposed) return;
            lock (_writeLock)
            {
                try
                {
                    using var conn = OpenConnection();
                    using var cmd = new SQLiteCommand(
                        @"INSERT INTO conversation (role, text, thinking, run_id, agent_type, tool_name, tool_input, is_tool_error, tool_duration_ms, timestamp, game_day)
                          VALUES (@role, @text, @thinking, @runId, @agentType, @toolName, @toolInput, @isToolError, @toolDurationMs, @ts, @gameDay)", conn);
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
                    cmd.ExecuteNonQuery();
                }
                catch (Exception ex)
                {
                    CoreLog.Warn($"[SqliteConvStore] 写入失败: {ex.Message}");
                }
            }
        }

        public ConversationEntry? GetAt(long id)
        {
            if (_disposed) return null;
            try
            {
                using var conn = OpenConnection();
                using var cmd = new SQLiteCommand(
                    "SELECT id, role, text, thinking, run_id, agent_type, tool_name, tool_input, is_tool_error, tool_duration_ms, timestamp, game_day FROM conversation WHERE id = @id", conn);
                cmd.Parameters.AddWithValue("@id", id);
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                    return ReadEntry(reader);
                return null;
            }
            catch (Exception ex)
            {
                CoreLog.Warn($"[SqliteConvStore] GetAt({id}) 失败: {ex.Message}");
                return null;
            }
        }

        public IReadOnlyList<ConversationEntry> GetRecent(int n)
        {
            if (_disposed) return Array.Empty<ConversationEntry>();
            try
            {
                using var conn = OpenConnection();
                using var cmd = new SQLiteCommand(
                    "SELECT id, role, text, thinking, run_id, agent_type, tool_name, tool_input, is_tool_error, tool_duration_ms, timestamp, game_day FROM conversation ORDER BY id DESC LIMIT @n", conn);
                cmd.Parameters.AddWithValue("@n", Math.Max(1, n));
                var list = new List<ConversationEntry>();
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    list.Add(ReadEntry(reader));
                // ORDER BY id DESC → 倒序插入 → 反转回升序
                list.Reverse();
                return list;
            }
            catch (Exception ex)
            {
                CoreLog.Warn($"[SqliteConvStore] GetRecent({n}) 失败: {ex.Message}");
                return Array.Empty<ConversationEntry>();
            }
        }

        public IReadOnlyList<ConversationEntry> GetBefore(long beforeId, int n)
        {
            if (_disposed) return Array.Empty<ConversationEntry>();
            try
            {
                using var conn = OpenConnection();
                using var cmd = new SQLiteCommand(
                    "SELECT id, role, text, thinking, run_id, agent_type, tool_name, tool_input, is_tool_error, tool_duration_ms, timestamp, game_day FROM conversation WHERE id < @beforeId ORDER BY id DESC LIMIT @n", conn);
                cmd.Parameters.AddWithValue("@beforeId", beforeId);
                cmd.Parameters.AddWithValue("@n", Math.Max(1, n));
                var list = new List<ConversationEntry>();
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    list.Add(ReadEntry(reader));
                list.Reverse();
                return list;
            }
            catch (Exception ex)
            {
                CoreLog.Warn($"[SqliteConvStore] GetBefore({beforeId}, {n}) 失败: {ex.Message}");
                return Array.Empty<ConversationEntry>();
            }
        }

        private static ConversationEntry ReadEntry(SQLiteDataReader reader)
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

        private void InitTable()
        {
            try
            {
                using var conn = OpenConnection();
                using var cmd = new SQLiteCommand(
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
                        game_day    INTEGER NOT NULL DEFAULT 0
                    );
                    CREATE INDEX IF NOT EXISTS idx_timestamp ON conversation(timestamp);
                    CREATE INDEX IF NOT EXISTS idx_game_day ON conversation(game_day);", conn);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                CoreLog.Error($"[SqliteConvStore] 建表失败: {ex.Message}");
                throw;
            }
        }

        private SQLiteConnection OpenConnection()
        {
            var conn = new SQLiteConnection(_connectionString);
            conn.Open();
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

        public IReadOnlyList<ConversationEntry> QueryToolCalls(
            string? toolName = null, int fromDay = 0, int toDay = int.MaxValue,
            int limit = 100, long beforeId = long.MaxValue)
        {
            if (_disposed) return Array.Empty<ConversationEntry>();
            try
            {
                using var conn = OpenConnection();
                using var cmd = new SQLiteCommand(
                    @"SELECT id, role, text, thinking, run_id, agent_type, tool_name, tool_input, is_tool_error, tool_duration_ms, timestamp, game_day
                      FROM conversation
                      WHERE role = 'tool_call'
                        AND (@tool IS NULL OR tool_name = @tool)
                        AND (@fromDay = 0 OR game_day >= @fromDay)
                        AND (@toDay = 2147483647 OR game_day <= @toDay)
                        AND id < @beforeId
                      ORDER BY id DESC
                      LIMIT @limit", conn);
                cmd.Parameters.AddWithValue("@tool", (object?)toolName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@fromDay", fromDay);
                cmd.Parameters.AddWithValue("@toDay", toDay);
                cmd.Parameters.AddWithValue("@beforeId", beforeId);
                cmd.Parameters.AddWithValue("@limit", Math.Max(1, limit));
                var list = new List<ConversationEntry>();
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    list.Add(ReadEntry(reader));
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
                using var conn = OpenConnection();
                using var cmd = new SQLiteCommand(
                    @"SELECT game_day, tool_name, COUNT(*) AS call_count
                      FROM conversation
                      WHERE role = 'tool_call'
                        AND game_day > 0
                        AND (@fromDay = 0 OR game_day >= @fromDay)
                        AND (@toDay = 2147483647 OR game_day <= @toDay)
                      GROUP BY game_day, tool_name
                      ORDER BY game_day DESC, call_count DESC", conn);
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
                using var cmd = new SQLiteCommand(
                    @"SELECT DISTINCT tool_name FROM conversation
                      WHERE role = 'tool_call' AND tool_name != ''
                      ORDER BY tool_name", conn);
                var list = new List<string>();
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    list.Add(reader.GetString(0));
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
            // SQLite 连接池由 .NET 管理，无需显式清理
        }
    }
}
