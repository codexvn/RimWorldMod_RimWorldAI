using System;
using System.IO;
using Microsoft.Data.Sqlite;
using RimWorldAgent.Core.AgentRuntime;

namespace RimWorldAgent.Core.Data
{
    public sealed class SqliteToolResultSnapshotStore : IToolResultSnapshotStore, IDisposable
    {
        private readonly string _connectionString;
        private readonly object _writeLock = new object();
        private bool _disposed;

        public SqliteToolResultSnapshotStore(string filePath)
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            _connectionString = $"Data Source={filePath}";
            CoreLog.Info($"[ToolResultSnapshotStore] DB: {filePath}");
            InitTable();
        }

        public ToolResultSnapshot? Get(string cacheKey)
        {
            if (_disposed || string.IsNullOrWhiteSpace(cacheKey)) return null;

            try
            {
                using var conn = OpenConnection();
                using var cmd = new SqliteCommand(
                    @"SELECT cache_key, tool_name, input_json, output_text, version
                      FROM tool_result_snapshot
                      WHERE cache_key = @cacheKey", conn);
                cmd.Parameters.AddWithValue("@cacheKey", cacheKey);
                using var reader = cmd.ExecuteReader();
                if (!reader.Read()) return null;

                return new ToolResultSnapshot
                {
                    CacheKey = reader.GetString(0),
                    ToolName = reader.GetString(1),
                    InputJson = reader.GetString(2),
                    OutputText = reader.GetString(3),
                    Version = reader.GetInt64(4)
                };
            }
            catch (Exception ex)
            {
                CoreLog.Warn($"[ToolResultSnapshotStore] 读取失败: {FormatExceptionChain(ex)}");
                return null;
            }
        }

        public void Upsert(ToolResultSnapshot snapshot)
        {
            if (_disposed || string.IsNullOrWhiteSpace(snapshot.CacheKey)) return;

            lock (_writeLock)
            {
                try
                {
                    using var conn = OpenConnection();
                    using var cmd = new SqliteCommand(
                        @"INSERT OR REPLACE INTO tool_result_snapshot
                            (cache_key, tool_name, input_json, output_text, version)
                          VALUES
                            (@cacheKey, @toolName, @inputJson, @outputText, @version)", conn);
                    cmd.Parameters.AddWithValue("@cacheKey", snapshot.CacheKey);
                    cmd.Parameters.AddWithValue("@toolName", snapshot.ToolName ?? "");
                    cmd.Parameters.AddWithValue("@inputJson", snapshot.InputJson ?? "");
                    cmd.Parameters.AddWithValue("@outputText", snapshot.OutputText ?? "");
                    cmd.Parameters.AddWithValue("@version", snapshot.Version);
                    cmd.ExecuteNonQuery();
                }
                catch (Exception ex)
                {
                    CoreLog.Warn($"[ToolResultSnapshotStore] 写入失败: {FormatExceptionChain(ex)}");
                }
            }
        }

        public void Clear()
        {
            if (_disposed) return;

            lock (_writeLock)
            {
                try
                {
                    using var conn = OpenConnection();
                    using var cmd = new SqliteCommand("DELETE FROM tool_result_snapshot", conn);
                    cmd.ExecuteNonQuery();
                    CoreLog.Info("[ToolResultSnapshotStore] 已清空工具结果快照");
                }
                catch (Exception ex)
                {
                    CoreLog.Warn($"[ToolResultSnapshotStore] 清空失败: {FormatExceptionChain(ex)}");
                }
            }
        }

        public void Dispose()
        {
            _disposed = true;
        }

        private void InitTable()
        {
            try
            {
                using var conn = OpenConnection();
                using var cmd = new SqliteCommand(
                    @"CREATE TABLE IF NOT EXISTS tool_result_snapshot (
                        cache_key    TEXT PRIMARY KEY,
                        tool_name    TEXT NOT NULL,
                        input_json   TEXT NOT NULL DEFAULT '',
                        output_text  TEXT NOT NULL,
                        version      INTEGER NOT NULL
                    );", conn);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                CoreLog.Error($"[ToolResultSnapshotStore] 建表失败: {FormatExceptionChain(ex)}");
                throw;
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

        private static string FormatExceptionChain(Exception ex)
        {
            var message = $"{ex.GetType().Name}: {ex.Message}";
            for (var inner = ex.InnerException; inner != null; inner = inner.InnerException)
                message += $" <- {inner.GetType().Name}: {inner.Message}";
            return message;
        }
    }
}
