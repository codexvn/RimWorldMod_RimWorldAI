using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RimWorldMCP
{
    public static class McpClient
    {
        private static HttpClient _http = new();
        private static string _endpoint = "";
        private static bool _initialized;
        private static int _nextId = 100;

        /// <summary>连接到远程 MCP 服务器</summary>
        public static async Task<bool> Connect(string endpoint, string token, string password)
        {
            _endpoint = endpoint.TrimEnd('/');
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            if (!string.IsNullOrEmpty(token))
                _http.DefaultRequestHeaders.Add("token", token);
            if (!string.IsNullOrEmpty(password))
                _http.DefaultRequestHeaders.Add("password", password);

            try
            {
                // initialize
                var initReq = new { jsonrpc = "2.0", id = (++_nextId).ToString(),
                    method = "initialize",
                    @params = new { protocolVersion = "2024-11-05", clientInfo = new { name = "RimWorldMCP", version = "1.0" } }
                };
                var initResp = await Post(initReq);
                if (initResp == null) return false;

                // notifications/initialized
                await Post(new { jsonrpc = "2.0", method = "notifications/initialized" });

                _initialized = true;
                McpLog.Info($"[client] 已连接到 {endpoint}");
                return true;
            }
            catch (Exception ex)
            {
                McpLog.Warn($"[client] 连接失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>发送消息到远程 MCP 服务器（调用 send_message 工具）</summary>
        public static async Task<bool> SendMessage(string message)
        {
            if (!_initialized || string.IsNullOrEmpty(_endpoint))
                return false;

            try
            {
                var req = new
                {
                    jsonrpc = "2.0",
                    id = (++_nextId).ToString(),
                    method = "tools/call",
                    @params = new
                    {
                        name = "send_message",
                        arguments = new Dictionary<string, object>
                        {
                            { "message", message }
                        }
                    }
                };
                var resp = await Post(req);
                return resp != null;
            }
            catch (Exception ex)
            {
                McpLog.Warn($"[client] 发送消息失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>通用工具调用</summary>
        public static async Task<string?> CallTool(string toolName, Dictionary<string, object> args)
        {
            if (!_initialized) return null;
            try
            {
                var req = new
                {
                    jsonrpc = "2.0",
                    id = (++_nextId).ToString(),
                    method = "tools/call",
                    @params = new { name = toolName, arguments = args }
                };
                return await Post(req);
            }
            catch (Exception ex)
            {
                McpLog.Warn($"[client] 调用 {toolName} 失败: {ex.Message}");
                return null;
            }
        }

        public static bool IsConnected => _initialized;

        private static async Task<string?> Post(object payload)
        {
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            });
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync(_endpoint, content);
            return resp.IsSuccessStatusCode ? await resp.Content.ReadAsStringAsync() : null;
        }
    }
}
